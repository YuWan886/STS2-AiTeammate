using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using AiTeammate.AiTeammateCode.Agent;
using AiTeammate.AiTeammateCode.Backend;
using AiTeammate.AiTeammateCode.Cache;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Chat;
using AiTeammate.AiTeammateCode.Context;
using AiTeammate.AiTeammateCode.Decision;
using AiTeammate.AiTeammateCode.Personality;
using AiTeammate.AiTeammateCode.Settings;
using AiTeammate.AiTeammateCode.Solo;
using AiTeammate.AiTeammateCode.Teamwork;
using AiTeammate.AiTeammateCode.Patches;
using AiTeammate.AiTeammateCode.Localization;
using AiTeammate.AiTeammateCode.Voice;
using MegaCrit.Sts2.Core.Runs;

namespace AiTeammate.AiTeammateCode;

public class AiTeammateManager
{
    private const int MaxCombatDecisionPassesPerTurn = 3;

    /// <summary>
    /// dsh answers every pass itself and the executor no longer auto-plays leftover cards, so a turn
    /// with draw effects needs more than one decision. Passes are only spent while energy and a
    /// playable card remain, so this cap only guards against pathological draw loops.
    /// 外部 Agent 当大脑时，每轮抽到的新牌都要再决策一次。
    /// </summary>
    private const int MaxExternalAgentCombatDecisionPassesPerTurn = 6;

    /// <summary>Extra hard-timeout budget per decision pass beyond the first (dsh round trips).</summary>
    private const int ExternalAgentCombatTurnSecondsPerExtraPass = 90;
    private static readonly Lazy<AiTeammateManager> _instance = new(() => new());
    public static AiTeammateManager Instance => _instance.Value;

    public BackendManager BackendManager { get; private set; } = null!;
    public PersonalityManager PersonalityManager { get; private set; } = null!;
    public ContentCacheService CacheService { get; private set; } = null!;
    public ContentQueryService QueryService { get; private set; } = null!;
    public DecisionService DecisionService { get; private set; } = null!;
    public ChatManager ChatManager { get; private set; } = null!;
    public ConversationContextManager ConversationContext { get; private set; } = null!;
    public PlayerDirectiveManager DirectiveManager { get; private set; } = null!;
    public CombatDecisionExecutor CombatExecutor { get; private set; } = null!;
    public OutOfCombatExecutor OutOfCombatExecutor { get; private set; } = null!;
    public DecisionValidator Validator { get; private set; } = null!;

    public AiLogger AiLog { get; private set; } = null!;
    public Player? AiPlayer { get; private set; }
    public bool IsInitialized { get; private set; }

    /// <summary>True when the AI drives the local player in a solo climb. 单人爬塔接管中。</summary>
    public bool IsSoloControlled { get; private set; }

    private readonly Lock _turnLock = new();
    private readonly Lock _restSiteUpgradeLock = new();
    private CancellationTokenSource? _turnCts;
    private ulong _staleBindingNotifiedNetId;
    private List<int>? _pendingRestSiteUpgradeIndices;
    private bool _hasTriggeredExtraPlayerTurnCombatDecision;
    private bool _pendingExtraPlayerTurnCombatDecision;

    private AiTeammateManager() { }

    public void Initialize()
    {
        // Idempotent: the bootstrap, NGame._Ready (other game versions) and the settings page can all
        // reach this during startup; re-running it would throw away chat history and the AI binding.
        if (IsInitialized)
            return;

        // Backend instances are rebuilt on demand from the live RitsuLib settings.
        BackendManager = new BackendManager();
        BackendManager.SyncFromSettings();

        // Cache
        CacheService = new ContentCacheService();
        CacheService.Initialize();
        QueryService = new ContentQueryService(CacheService);

        // Personality
        PersonalityManager = new PersonalityManager();
        PersonalityManager.Load();

        // Logger
        AiLog = new AiLogger();
        AiLog.Initialize();

        ConversationContext = new ConversationContextManager();
        DirectiveManager = new PlayerDirectiveManager();

        // Services
        DecisionService = new DecisionService(BackendManager, PersonalityManager, QueryService, ConversationContext);
        ChatManager = new ChatManager(BackendManager, PersonalityManager, QueryService, ConversationContext, DirectiveManager);
        CombatExecutor = new CombatDecisionExecutor(AiLog);
        OutOfCombatExecutor = new OutOfCombatExecutor(DecisionService);
        Validator = new DecisionValidator();

        IsInitialized = true;
        SoloRunController.InstallRunHook();

        // MiniMax TTS: listens to the chat transcript so every AI line path (combat, auto-chat,
        // player replies, dsh) is covered without touching each call site.
        try
        {
            TtsService.Instance.Attach(ChatManager);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[VOICE] 语音服务初始化失败: {ex.Message}");
        }

        MainFile.Logger.Info("AI 队友管理器初始化完成");
        AiLog.Log("SYSTEM", "AI 队友管理器初始化完成");

        // HextechRunes integration (soft dependency)
        try
        {
            // Also waits for the Hextech assembly when it has not been loaded yet (load order is not guaranteed).
            HextechRunesPatchInstaller.Initialize();
            if (HextechRunesDecisionHandler.IsAvailable)
                AiLog.Log("SYSTEM", "海克斯符文集成已启用");
            else
                MainFile.Logger.Info("[HextechRunes] 暂未检测到海克斯符文mod，已注册延迟检测");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[HextechRunes] 集成初始化失败: {ex.Message}");
        }
    }

    /// <summary>
    /// Initializes once and never throws, so a caller can retry later instead of leaving the mod dead.
    /// 安全初始化：失败时不抛异常，允许之后重试。
    /// </summary>
    public bool TryInitialize()
    {
        if (IsInitialized)
            return true;

        try
        {
            Initialize();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Init] AI 队友运行时初始化失败，稍后重试: {ex.Message}");
        }

        return IsInitialized;
    }

    public void OnAiPlayerJoined(Player aiPlayer)
    {
        ApplyAiPlayerBinding(aiPlayer, announce: true);
    }

    /// <summary>
    /// Rebinds the cached AI player to the instance the live run actually uses. Loading a save
    /// rebuilds every Player, so the old reference is no longer in <c>RunState.Players</c>:
    /// <c>GetPlayerSlotIndex(Player)</c> then returns -1 and every per-player lookup (events,
    /// map votes, combat turn detection) throws or silently never matches.
    /// 读档后 RunState 换成了新的 Player 实例，旧引用必须换掉。
    /// </summary>
    public bool RebindAiPlayer(Player aiPlayer, string reason)
    {
        if (ReferenceEquals(aiPlayer, AiPlayer))
            return false;

        var hadPrevious = AiPlayer != null;
        ApplyAiPlayerBinding(aiPlayer, announce: !hadPrevious);
        MainFile.Logger.Info(hadPrevious
            ? $"[{reason}] AI 玩家已重新绑定到新的 Player 实例（读档重建运行状态）: NetId={aiPlayer.NetId}"
            : $"[{reason}] AI 玩家已绑定: NetId={aiPlayer.NetId}");
        return true;
    }

    /// <summary>Finds the AI player among live Player instances; identity is the reserved NetId.</summary>
    private static Player? FindAiPlayer(IReadOnlyList<Player>? players)
    {
        if (players == null)
            return null;

        foreach (var player in players)
        {
            if (AiIdentity.IsAi(player.NetId))
                return player;
        }

        return null;
    }

    /// <summary>
    /// True when the cached AI player really takes part in the given combat. A binding left over from
    /// an earlier run keeps a Player whose Creature died with that run, and a fresh run reuses net ids,
    /// so identity has to be judged by instance membership, not by NetId. Signalling end-of-turn for
    /// such a leftover is not harmless: in singleplayer <c>AllPlayersReadyToEndTurn()</c> returns true
    /// without consulting the ready set, so the call ends the current player's turn immediately.
    /// </summary>
    private bool IsAiPlayerInCurrentCombat(CombatState state)
    {
        var aiPlayer = AiPlayer;
        if (aiPlayer == null)
            return false;

        lock (_turnLock)
        {
            if (state.Players.Contains(aiPlayer))
                return true;
        }

        // Solo climb binds the RunState Player instance, while a save load may rebuild the combat's
        // own instances. Accept the same NetId only while solo control is genuinely active; a leftover
        // from a finished run has solo control released and stays rejected.
        if (!IsSoloControlled)
            return false;

        return state.Players.Any(p => p.NetId == aiPlayer.NetId && p.Creature?.IsAlive == true);
    }

    /// <summary>Drops a binding that cannot belong to the run that is being set up now.</summary>
    private void ClearStaleAiPlayerBinding(string origin)
    {
        var stale = AiPlayer;
        if (stale == null)
            return;

        AiPlayer = null;
        MainFile.Logger.Info($"[{origin}] 清除过期的AI玩家绑定 (NetId={stale.NetId}, 角色={stale.Character?.Id.Entry})");
    }

    private void ApplyAiPlayerBinding(Player aiPlayer, bool announce)
    {
        AiPlayer = aiPlayer;
        ConversationContext.ResetAll();
        DirectiveManager.ResetAll();
        ResetExtraPlayerTurnCombatDecisionState();

        if (announce)
        {
            var speakerName = AiNames.DisplayName;
            ChatManager.AddSystemMessage(AiLoc.T("system.ai_joined", "{Name} joined the team", ("Name", speakerName)));
            Patches.NGamePatch.RefreshChatUi();
            MainFile.Logger.Info($"AI 玩家已加入: {aiPlayer.Character?.Id.Entry}");
            AiLog.Log("SYSTEM", $"AI 玩家加入: {aiPlayer.Character?.Id.Entry} (NetId={aiPlayer.NetId})");
        }

        // Multiplayer: let every client resolve the same nickname for this AI player.
        try
        {
            AiNicknameSync.OnAiPlayerJoined();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Nickname] 联机昵称同步初始化失败: {ex.Message}");
        }

        // Multiplayer: register the AI replication channel and let the host seed every client with
        // its authoritative copy of the AI player (deck/relics/relic grab bag included).
        try
        {
            AiSyncBus.EnsureRegistered();
            AiSyncBus.ResetForNewRun();
            AiStateSync.ClearPending();
            if (AiMultiplayer.IsAuthority)
                MainThread.Post(() => AiStateSync.BroadcastFromAuthority("ai-joined"));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sync] AI 同步初始化失败: {ex.Message}");
        }

        // A run with an AI teammate invites a running dsh to open a conversation and take over.
        AgentTakeoverInvite.OnAiPlayerJoined(aiPlayer.Character?.Id.Entry ?? "");
    }

    /// <summary>Binds the local singleplayer player to AI control (solo climb mode).</summary>
    public void BindSoloPlayer(Player player)
    {
        IsSoloControlled = true;
        OnAiPlayerJoined(player);
    }

    /// <summary>Stops AI control of the local player (mode disabled, run ended).</summary>
    public void ReleaseSoloControl()
    {
        if (!IsSoloControlled)
            return;

        IsSoloControlled = false;
        AiPlayer = null;
        MainFile.Logger.Info("AI 已停止控制本地玩家");
        AiLog.Log("SYSTEM", "AI 已释放本地玩家控制权");
    }

    public void ResetExtraPlayerTurnCombatDecisionState()
    {
        lock (_turnLock)
        {
            _hasTriggeredExtraPlayerTurnCombatDecision = false;
            _pendingExtraPlayerTurnCombatDecision = false;
        }
    }

    public bool HasExtraPlayerTurnCombatDecisionOpportunity()
    {
        var aiPlayer = AiPlayer;
        var combat = aiPlayer?.PlayerCombatState;
        if (!IsInitialized || aiPlayer?.Creature == null || combat == null)
            return false;

        if (!aiPlayer.Creature.IsAlive || combat.Hand.Cards.Count == 0)
            return false;

        // The built-in executor only asks again when there is energy left; an external agent also
        // gets a pass for free / star-only hands, since it never auto-plays leftovers.
        if (!AgentDecisionBroker.Enabled && combat.Energy <= 0)
            return false;

        var state = CombatManager.Instance.DebugOnlyGetState();
        if (CombatManager.Instance.IsOverOrEnding || state?.CurrentSide != CombatSide.Player)
            return false;

        return combat.Hand.Cards.Any(card =>
            !card.Keywords.Contains(CardKeyword.Unplayable)
            && combat.HasEnoughResourcesFor(card, out _));
    }

    public bool TryPrepareExtraPlayerTurnCombatDecision()
    {
        lock (_turnLock)
        {
            if (_hasTriggeredExtraPlayerTurnCombatDecision || _pendingExtraPlayerTurnCombatDecision)
                return false;

            _hasTriggeredExtraPlayerTurnCombatDecision = true;
            _pendingExtraPlayerTurnCombatDecision = true;
            return true;
        }
    }

    public void CancelPendingExtraPlayerTurnCombatDecision()
    {
        lock (_turnLock)
        {
            _hasTriggeredExtraPlayerTurnCombatDecision = false;
            _pendingExtraPlayerTurnCombatDecision = false;
        }
    }

    public bool ConsumePendingExtraPlayerTurnCombatDecision()
    {
        lock (_turnLock)
        {
            if (!_pendingExtraPlayerTurnCombatDecision)
                return false;

            _pendingExtraPlayerTurnCombatDecision = false;
            return true;
        }
    }

    /// <summary>
    /// Add AI-controlled LobbyPlayer to the multiplayer lobby's Players list.
    /// </summary>
    public LobbyPlayer? AddAiToLobby(StartRunLobby lobby, CharacterModel? character = null)
    {
        if (!IsInitialized) return null;

        // One AI teammate at most. A second Add used to append another fake entry with the next
        // slot's NetId; those ghosts stayed in the lobby list, inflated the player count and made
        // vanilla targeted sends address peers that do not exist.
        var existing = GetAiLobbyPlayer(lobby);
        if (existing.HasValue)
        {
            if (character != null)
                SetAiCharacter(lobby, character);
            MainFile.Logger.Info("AI already in lobby, reusing the existing entry");
            return GetAiLobbyPlayer(lobby);
        }

        // Reset AI player for a new run (old reference is stale)
        AiPlayer = null;

        if (lobby.Players.Count >= Compat.GameCompat.MaxPlayers(lobby))
        {
            MainFile.Logger.Warn($"Lobby is full ({lobby.Players.Count}/{Compat.GameCompat.MaxPlayers(lobby)})");
            return null;
        }

        int slot = -1;
        for (int i = 0; i < Compat.GameCompat.MaxPlayers(lobby); i++)
        {
            if (!lobby.Players.Any(p => p.slotId == i))
            {
                slot = i;
                break;
            }
        }
        if (slot < 0) return null;

        ulong aiNetId = AiIdentity.NetIdBase + (ulong)slot;

        var chars = GetAiCharacterOptions();
        if (chars.Count == 0) return null;
        character ??= ResolvePreferredAiCharacter(chars) ?? chars[0];

        var hostPlayer = lobby.Players.FirstOrDefault();
        var aiLobbyPlayer = new LobbyPlayer
        {
            id = aiNetId,
            slotId = slot,
            character = character,
            unlockState = hostPlayer.unlockState,
            maxMultiplayerAscensionUnlocked = hostPlayer.maxMultiplayerAscensionUnlocked,
#if STS2_API_0_111_OR_LATER
            isModded = true,
#endif
            isReady = false
        };

        lobby.Players.Add(aiLobbyPlayer);
        MainFile.Logger.Info($"AI added to lobby: slot {slot}, char {character.Id.Entry}");
        return aiLobbyPlayer;
    }

    public void RemoveAiFromLobby(StartRunLobby lobby)
    {
        // Remove every AI entry, not just the first: older builds could leave duplicates behind.
        var removed = lobby.Players.RemoveAll(p => AiIdentity.IsAi(p.id));
        if (removed > 0)
        {
            MainFile.Logger.Info($"AI removed from lobby ({removed} entry/entries)");
        }
    }

    public void SetAiCharacter(StartRunLobby lobby, CharacterModel character)
    {
        for (int i = 0; i < lobby.Players.Count; i++)
        {
            if (AiIdentity.IsAi(lobby.Players[i].id))
            {
                var p = lobby.Players[i];
                p.character = character;
                lobby.Players[i] = p;
                RememberAiCharacter(character);
                return;
            }
        }
    }

    /// <summary>
    /// Character the AI teammate is added with when a lobby is created: the host's last pick, or
    /// <c>null</c> when nothing was picked yet or the saved character is gone (mod removed).
    /// </summary>
    private static CharacterModel? ResolvePreferredAiCharacter(List<CharacterModel> options)
    {
        try
        {
            var id = AiSettingsStore.Current.AiLobbyCharacterId;
            if (string.IsNullOrWhiteSpace(id))
                return null;

            return options.FirstOrDefault(c =>
                !(c is RandomCharacter)
                && string.Equals(c.Id.Entry, id, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // Missing store during startup: fall back to the caller's default character.
            return null;
        }
    }

    /// <summary>Persists the AI's lobby character so the next lobby reuses it.</summary>
    private static void RememberAiCharacter(CharacterModel character)
    {
        try
        {
            if (character is RandomCharacter)
                return;

            var settings = AiSettingsStore.Current;
            if (string.Equals(settings.AiLobbyCharacterId, character.Id.Entry, StringComparison.Ordinal))
                return;

            settings.AiLobbyCharacterId = character.Id.Entry;
            AiSettingsStore.Save();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Lobby] could not persist the AI teammate character: {ex.Message}");
        }
    }

    public void SetAiReady(StartRunLobby lobby, bool ready)
    {
        for (int i = 0; i < lobby.Players.Count; i++)
        {
            if (AiIdentity.IsAi(lobby.Players[i].id))
            {
                var p = lobby.Players[i];
                p.isReady = ready;
                lobby.Players[i] = p;
                return;
            }
        }
    }

    public bool IsAiInLobby(StartRunLobby lobby)
    {
        return lobby.Players.Any(p => AiIdentity.IsAi(p.id));
    }

    public LobbyPlayer? GetAiLobbyPlayer(StartRunLobby lobby)
    {
        var p = lobby.Players.FirstOrDefault(p => AiIdentity.IsAi(p.id));
        return p.id != 0 ? p : null;
    }

    public List<CharacterModel> GetAiCharacterOptions()
    {
        try
        {
            return ModelDb.AllCharacters
                .Where(c => c is not RandomCharacter && !c.Id.Entry.Contains("Random", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (KeyNotFoundException)
        {
            // The settings page is registered while the game is still starting up, before
            // ModelDb.Initialize() fills its model dictionary, so Character<T>() throws here.
            // Report "no characters yet" instead of a warning; the settings UI and the dsh bridge
            // call this provider again once the models exist.
            return new List<CharacterModel>();
        }
    }

    /// <summary>
    /// Detect the AI player in a combat state after the run has started. The combat state's Player
    /// instances are authoritative: after a save load they differ from the cached one, and the stale
    /// reference must be replaced before any per-player lookup runs.
    /// </summary>
    public void DetectAndBindAiPlayer(CombatState state)
    {
        var live = FindAiPlayer(state.Players);
        if (live != null)
        {
            RebindAiPlayer(live, "Combat");
            return;
        }

        // No AI-identity player is in this combat. The manager is process-wide while players belong
        // to one run, so a binding whose NetId is not among the live players is a leftover from an
        // earlier run and must be dropped instead of being reused for the new one.
        if (AiPlayer != null && state.Players.Count > 0
            && !state.Players.Any(p => p.NetId == AiPlayer.NetId))
        {
            ClearStaleAiPlayerBinding("Combat");
        }

        if (AiPlayer != null) return;

        SoloRunController.TryBind(this);
    }

    /// <summary>
    /// Detect the AI player from RunState (for non-combat scenarios like Neow event). Re-checks the
    /// cached binding every call so a save load can never leave a stale Player behind.
    /// </summary>
    public void DetectAndBindAiPlayer()
    {
        if (AiPlayer != null)
            SoloRunController.TryBind(this); // keeps solo control in sync with the settings toggle

        var state = RunManager.Instance?.DebugOnlyGetState();
        var live = FindAiPlayer(state?.Players);
        if (live != null)
        {
            RebindAiPlayer(live, "Detect");
            return;
        }

        // Same leftover rule as the combat overload: a binding that is not part of the run that is
        // loaded right now belongs to a finished run (fresh runs reuse NetIds, the player objects do not).
        if (AiPlayer != null && state is { Players.Count: > 0 }
            && !state.Players.Any(p => p.NetId == AiPlayer.NetId))
        {
            ClearStaleAiPlayerBinding("Detect");
        }

        if (AiPlayer != null) return;

        SoloRunController.TryBind(this);
    }

    /// <summary>
    /// Produces one combat decision pass. The host runs the LLM and broadcasts the plan; every
    /// client waits for that plan and executes it verbatim so both simulations stay identical.
    /// Outside a networked multiplayer run this is just the normal local LLM call.
    /// </summary>
    private async Task<DecisionResult> DecideCombatPlanAsync(int decisionPass, CancellationToken ct)
    {
        var aiPlayer = AiPlayer;
        if (aiPlayer == null)
            return DecisionResult.Fallback();

        if (!AiMultiplayer.RequiresDecisionSync)
        {
            return await Task.Run(async () => await DecisionService.DecideCombatAsync(aiPlayer, ct), ct);
        }

        if (AiMultiplayer.IsAuthority)
        {
            var own = await Task.Run(async () => await DecisionService.DecideCombatAsync(aiPlayer, ct), ct);
            AiCombatPlanCodec.Publish(aiPlayer, decisionPass, own);
            return own;
        }

        // Follower: mirror the host's plan. The hard turn budget (45 s) is the upper bound, so a
        // missing plan means the host hit its own fallback — use the same deterministic fallback.
        AiSyncBus.EnsureRegistered();
        var key = AiCombatPlanCodec.Key(aiPlayer, decisionPass);
        AiLog.Log("COMBAT", $"等待房主战斗决策 ({key})");
        var json = await AiSyncBus.AwaitAsync(AiSyncBus.KindCombatPlan, key, TimeSpan.FromSeconds(40), ct);
        var plan = AiCombatPlanCodec.Deserialize(json);
        if (plan == null)
        {
            AiLog.LogDecisionFailure("未收到房主的战斗计划，使用本地兜底");
            return DecisionResult.Fallback();
        }

        return plan;
    }

    public async Task ProcessCombatTurnAsync()
    {
        if (AiPlayer == null || !IsInitialized) return;

        // Don't process turns if combat is already over or ending.
        // TurnStarted can fire during reward processing when combat
        // state hasn't been fully cleaned up yet.
        if (CombatManager.Instance.IsOverOrEnding) return;

        // Cancel any in-flight turn processing from previous invocations
        _turnCts?.Cancel();
        _turnCts = new CancellationTokenSource();
        var ct = _turnCts.Token;

        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null) return;

        // A binding left over from an earlier run keeps a Player whose Creature died with that run.
        // Never let it drive turn handling: in singleplayer AllPlayersReadyToEndTurn() returns true
        // without looking at the ready set, so ANY SetReadyToEndTurn call below ends the human's
        // turn on the spot — the run then looks like it skips every single turn.
        if (!IsAiPlayerInCurrentCombat(state))
        {
            var staleNetId = AiPlayer?.NetId ?? 0;
            if (_staleBindingNotifiedNetId != staleNetId)
            {
                _staleBindingNotifiedNetId = staleNetId;
                AiLog.Log("SYSTEM", $"AI 玩家不属于当前战斗(NetId={staleNetId})，忽略本次回合处理");
            }
            return;
        }
        _staleBindingNotifiedNetId = 0;

        // === CRITICAL: AI death detection ===
        // If the AI died during the enemy turn, immediately end turn
        // so we don't deadlock the game waiting for a dead player.
        if (!AiPlayer.Creature.IsAlive)
        {
            AiLog.Log("SYSTEM", "AI已阵亡，跳过回合处理");
            try
            {
                // The player→enemy transition is signalled after end-of-turn phase one
                // (CombatTurnTransitionPatch); an extra signal here would start a second one.
                CombatManager.Instance.SetReadyToEndTurn(AiPlayer, false);
            }
            catch { }
            return;
        }

        // Additional guard: don't process if the AI has neither cards nor potions.
        // Potion-only turns are still meaningful, especially when a human explicitly
        // asked the AI to buff or heal them.
        var combat = AiPlayer.PlayerCombatState;
        var hasPotions = AiPlayer.Potions?.Any() == true;
        if (combat == null || (combat.Hand.Cards.Count == 0 && !hasPotions))
        {
            // End the turn immediately so we don't stall. The player→enemy transition is signalled
            // once after end-of-turn phase one (CombatTurnTransitionPatch).
            try
            {
                CombatManager.Instance.SetReadyToEndTurn(AiPlayer, false);
            }
            catch { }
            return;
        }

        // Solo climb: the human may have interrupted the AI to chat. Hold the turn (without
        // ending it) until they let the AI continue; SoloRunController restarts processing then.
        if (IsSoloControlled && AiFlowControl.IsPaused)
        {
            AiLog.Log("SYSTEM", "AI 已被玩家暂停，等待继续");
            return;
        }

        bool isAiTurn;
        lock (_turnLock)
        {
            isAiTurn = state.Players.Contains(AiPlayer) && state.CurrentSide == CombatSide.Player;
        }

        if (!isAiTurn) return;

        // Track whether we should force-end the turn in the finally block.
        // Set to true once we've confirmed this is the AI's turn to process.
        bool turnInProgress = true;
        bool shouldAdvanceDirectives = true;
        bool pausedHold = false;
        try
        {
            ct.ThrowIfCancellationRequested();

            // === Hard safety timeout for the entire turn processing ===
            // Without an external brain this is a last-resort deadlock net: the LLM call has its own
            // timeout and card execution takes <5s. When dsh is driving the AI, a turn legitimately
            // takes minutes (the agent has to read the prompt and answer) and can now hold several
            // decision passes (one per mid-turn draw), so both the pass count and the net widen.
            var maxDecisionPasses = AgentDecisionBroker.Enabled
                ? MaxExternalAgentCombatDecisionPassesPerTurn
                : MaxCombatDecisionPassesPerTurn;
            var turnBudgetSeconds = AgentDecisionBroker.Enabled
                ? 330 + ExternalAgentCombatTurnSecondsPerExtraPass * (maxDecisionPasses - 1)
                : 45;
            using var hardTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(turnBudgetSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, hardTimeoutCts.Token);
            var effectiveCt = linkedCts.Token;

            for (var decisionPass = 1; decisionPass <= maxDecisionPasses; decisionPass++)
            {
                effectiveCt.ThrowIfCancellationRequested();

                if (IsSoloControlled && AiFlowControl.IsPaused)
                {
                    pausedHold = true;
                    AiLog.Log("COMBAT", "玩家打断：暂停出牌，保留本回合");
                    break;
                }

                if (decisionPass > 1)
                {
                    if (!HasExtraPlayerTurnCombatDecisionOpportunity())
                        break;

                    AiLog.Log("COMBAT", $"检测到AI仍有能量和可打牌，继续发起第{decisionPass}次战斗决策");
                }

                // Run AI backend decision on background thread (HTTP call with timeout).
                // In multiplayer only the host may call the LLM for the AI: cards are auto-played
                // into each peer's local simulation (CardCmd.AutoPlay is never enqueued), so every
                // peer must execute the exact same plan or the run diverges.
                var result = await DecideCombatPlanAsync(decisionPass, effectiveCt);

                // Log the AI's decision
                if (result.IsFallback)
                {
                    AiLog.LogDecisionFailure("AI 后端返回无效响应或超时");
                }
                AiLog.LogDecision(result.Thinking, result.Actions);
                if (result.DeferredActions.Count > 0)
                    AiLog.LogDecision("", result.DeferredActions);

                // Push AI thinking to chat panel
                ChatManager.AddAiThought(result.Thinking);

                if (!string.IsNullOrWhiteSpace(result.ChatMessage))
                {
                    AiLog.LogChat(PersonalityManager.Active?.Name ?? "AI", result.ChatMessage);
                    ChatManager.AddAiMessage(result.ChatMessage, ChatMessageSource.Combat);

                    // Let the speech bubble render before card autoplay starts,
                    // otherwise the visual effect lands after the first card play.
                    await Task.Delay(ChatManager.GetSpeechLeadInDelayMs(result.ChatMessage), effectiveCt);
                }
                else if (decisionPass == 1 && AiMultiplayer.IsAuthority)
                {
                    var autoMessage = await ChatManager.TryAutoMessage(AiPlayer, "AiTurnStart", effectiveCt);
                    if (autoMessage != null)
                    {
                        await Task.Delay(ChatManager.GetSpeechLeadInDelayMs(autoMessage.Content), effectiveCt);
                    }
                }

                effectiveCt.ThrowIfCancellationRequested();

                if (IsSoloControlled && AiFlowControl.IsPaused)
                {
                    pausedHold = true;
                    AiLog.Log("COMBAT", "玩家打断：暂停出牌，保留本回合");
                    break;
                }

                // Back on main thread — validate and execute card actions
                var dropped = new List<string>();
                var validActions = result.IsFallback
                    ? Validator.ApplyFallback(AiPlayer)
                    : Validator.Validate(result.Actions, AiPlayer, dropped);
                var validDeferred = result.IsFallback
                    ? new List<DecisionAction>()
                    : Validator.ValidateDeferred(result.DeferredActions, AiPlayer, dropped);
                RecordDecisionFeedback(result, dropped, validActions);
                await CombatExecutor.Execute(validActions, validDeferred, AiPlayer, effectiveCt, endTurn: false);

                if (IsSoloControlled && (AiFlowControl.IsPaused || CombatExecutor.PausedMidTurn))
                {
                    pausedHold = true;
                    AiLog.Log("COMBAT", "玩家打断：暂停出牌，保留本回合");
                    break;
                }

                // The plan said "end turn": that is the brain's answer for this turn, don't spend
                // another decision just because resources are left. (The built-in executor's Phase 3
                // already auto-played its leftovers before we get here.) Read the raw plan, not
                // validActions — the validator injects an end_turn of its own when nothing was usable,
                // and that case still deserves another pass.
                if (!result.IsFallback && result.Actions.Any(action => action.Type == "end_turn"))
                {
                    AiLog.Log("COMBAT", "决策包含 end_turn，本回合不再发起新的战斗决策");
                    break;
                }
            }

            if (pausedHold)
            {
                // Keep the turn open: the human is talking to the AI. Resume restarts processing.
                turnInProgress = false;
                shouldAdvanceDirectives = false;
                return;
            }

            if (HasExtraPlayerTurnCombatDecisionOpportunity())
            {
                AiLog.Log("COMBAT", $"达到单回合最多 {maxDecisionPasses} 次战斗决策上限，结束本次AI行动");
            }

            if (CombatManager.Instance.IsInProgress
                && CombatManager.Instance.DebugOnlyGetState()?.CurrentSide == CombatSide.Player)
            {
                CombatExecutor.TryEndTurn(AiPlayer);
            }

            // Turn completed normally — finally block doesn't need to force-end.
            turnInProgress = false;
        }
        catch (OperationCanceledException)
        {
            AiLog.Log("SYSTEM", "回合处理被新回合中断");
            turnInProgress = false; // New turn started, don't force-end in finally
            shouldAdvanceDirectives = false;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"AI turn error: {ex.Message}");
            AiLog.LogError("回合处理", ex.Message);
        }
        finally
        {
            if (shouldAdvanceDirectives)
            {
                try
                {
                    DirectiveManager.AdvanceAfterAiTurn();
                }
                catch (Exception directiveEx)
                {
                    MainFile.Logger.Warn($"Directive advance error: {directiveEx.Message}");
                }
            }

            // Safety net: if combat is still active and it's still the player's turn,
            // force-end the AI's turn so we never deadlock the game.
            // This handles cases where card execution throws mid-turn (e.g., mod conflicts).
            if (turnInProgress)
            {
                try
                {
                    // Use IsInProgress instead of !IsOverOrEnding: when all enemies are dead,
                    // IsEnding is true but IsInProgress is still true until EndCombatInternal.
                    // We must still signal end-turn so AllPlayersReadyToEndTurn can trigger
                    // the final CheckWinCondition → EndCombatInternal transition.
                    if (CombatManager.Instance.IsInProgress)
                    {
                        var curState = CombatManager.Instance.DebugOnlyGetState();
                        if (curState?.CurrentSide == CombatSide.Player
                            && curState.Players.Contains(AiPlayer))
                        {
                            AiLog.Log("SYSTEM", "安全网：强制结束AI回合");
                            CombatManager.Instance.SetReadyToEndTurn(AiPlayer, false);
                        }
                    }
                }
                catch (Exception finalEx)
                {
                    MainFile.Logger.Warn($"AI force-end-turn error: {finalEx.Message}");
                }
            }
        }
    }

    /// <summary>Records what the validator did with dsh's last plan, for the next snapshot.</summary>
    private static void RecordDecisionFeedback(DecisionResult result, List<string> dropped,
        List<DecisionAction> validActions)
    {
        if (!AgentDecisionBroker.Enabled)
            return;

        try
        {
            if (result.IsFallback && dropped.Count == 0)
                dropped.Add("整个决策无效或超时，已使用本地兜底");

            var requested = result.Actions
                .Where(action => action.Type is not ("end_turn" or "suggest_card"))
                .Select(DescribeAgentAction)
                .Where(text => text.Length > 0)
                .ToList();

            // Which exact creature each kept action locked in ("Card->Cultist[#1]" for enemies):
            // without this dsh cannot tell whether it hit the twin it planned for.
            var state = CombatManager.Instance.DebugOnlyGetState();
            var resolved = validActions
                .Where(action => action.ResolvedTarget != null)
                .Select(action =>
                    $"{action.CardName}->{CombatTargetResolver.Describe(state, action.ResolvedTarget)}")
                .ToList();

            var valid = validActions.Count(action => action.Type is not "end_turn");
            AgentDecisionFeedback.Record(AgentDecisionBroker.LastResolvedId,
                AgentDecisionBroker.LastResolvedKind, requested, dropped, valid, resolved);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Agent] decision feedback failed: {ex.Message}");
        }
    }

    private static string DescribeAgentAction(DecisionAction action) => action.Type switch
    {
        "play_card" => string.IsNullOrWhiteSpace(action.Target)
            ? action.CardName
            : $"{action.CardName}->{action.Target}",
        "use_potion" => $"药水:{action.PotionName}",
        _ => action.Type
    };

    public void SetPendingRestSiteUpgradeIndices(List<int>? indices)
    {
        lock (_restSiteUpgradeLock)
        {
            _pendingRestSiteUpgradeIndices = indices is { Count: > 0 } ? new List<int>(indices) : null;
        }
    }

    public List<int>? ConsumePendingRestSiteUpgradeIndices()
    {
        lock (_restSiteUpgradeLock)
        {
            var result = _pendingRestSiteUpgradeIndices;
            _pendingRestSiteUpgradeIndices = null;
            return result is { Count: > 0 } ? new List<int>(result) : null;
        }
    }

    public async Task GenerateMemory(MemoryCategory category, string context, CancellationToken ct)
    {
        if (PersonalityManager.Active == null) return;
        if (!AiSettingsStore.Current.EnablePersonalityGrowth || !AiSettingsStore.Current.EnableLongTermMemory)
            return;

        var promptBuilder = new PersonalityPromptBuilder(PersonalityManager);
        var systemPrompt = promptBuilder.BuildSystemPrompt(requireJson: true);

        var userPrompt = $"基于以下事件，判断是否值得记住。如果是，生成一条记忆。\n\n事件类型：{category}\n触发上下文：{context}\n\n请以 JSON 格式返回（json output required）：{{\"should_remember\": true/false, \"content\": \"记忆内容（中文，50字以内）\", \"importance\": 1-10, \"trigger_context\": \"什么情况下回忆这条\"}}";

        var request = new AiRequest
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            Type = AiRequestType.Memory,
            MaxTokens = 2048,
            Temperature = 0.5f,
            EnableJsonMode = true
        };

        var response = await BackendManager.SendAsync(request, ct);
        if (!response.IsSuccess) return;

        try
        {
            using var doc = JsonDocument.Parse(response.Content);
            var root = doc.RootElement;
            if (root.TryGetProperty("should_remember", out var shouldRemember) && shouldRemember.GetBoolean())
            {
                var memoryManager = new MemoryManager();
                memoryManager.AddEntry(PersonalityManager.Active.Id, new MemoryEntry
                {
                    Category = category,
                    Importance = root.TryGetProperty("importance", out var imp) ? imp.GetInt32() : 5,
                    Content = root.TryGetProperty("content", out var cnt) ? cnt.GetString() ?? "" : "",
                    TriggerContext = root.TryGetProperty("trigger_context", out var tc) ? tc.GetString() ?? "" : "",
                    Timestamp = DateTime.UtcNow
                });
            }
        }
        catch { /* parsing failure - skip this memory */ }
    }

    public async Task OnCombatWon()
    {
        if (AiPlayer == null || PersonalityManager.Active == null) return;
        await GenerateMemory(MemoryCategory.CombatLesson, "战斗胜利", CancellationToken.None);
        CheckAndTriggerSelfReflection(PersonalityManager.Active.Id);
    }

    public void CheckAndTriggerSelfReflection(string profileId)
    {
        var settings = AiSettingsStore.Current;
        if (!settings.EnablePersonalityGrowth || !settings.EnableLongTermMemory)
            return;

        var memoryManager = new MemoryManager();
        var memory = memoryManager.Load(profileId);
        var threshold = Math.Max(20, settings.MemoryMergeThreshold);

        if (memory.Entries.Count < threshold) return;

        _ = TriggerSelfReflectionAsync(profileId);
    }

    private async Task TriggerSelfReflectionAsync(string profileId)
    {
        var promptBuilder = new PersonalityPromptBuilder(PersonalityManager);
        var systemPrompt = promptBuilder.BuildSystemPrompt(requireJson: true);
        var memoryManager = new MemoryManager();
        var memory = memoryManager.Load(profileId);

        var allMemories = string.Join("\n", memory.Entries.Select(m => $"- {m.Content}"));
        if (string.IsNullOrWhiteSpace(allMemories)) return;

        var userPrompt = $"你的长期记忆已积累 {memory.Entries.Count} 条。请总结这些记忆，提炼出关于你自己的2-3句话自我认知：\n\n记忆列表：\n{allMemories}\n\n返回JSON：{{\"self_reflection\": \"你的自我总结（中文，2-3句话）\"}}";

        var request = new AiRequest
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            Type = AiRequestType.SelfReflection,
            MaxTokens = 2048,
            Temperature = 0.9f,
            EnableJsonMode = true
        };

        var response = await BackendManager.SendAsync(request, CancellationToken.None);
        if (response.IsSuccess)
        {
            try
            {
                using var doc = JsonDocument.Parse(response.Content);
                var root = doc.RootElement;
                var reflection = root.TryGetProperty("self_reflection", out var sr) ? sr.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(reflection))
                    memoryManager.MergeMemories(profileId, reflection);
            }
            catch { }
        }
    }
}
