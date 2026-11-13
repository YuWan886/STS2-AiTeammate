using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Handlers;
using MegaCrit.Sts2.Core.AutoSlay.Handlers.Screens;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using AiTeammate.AiTeammateCode.Chat;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Patches;
using AiTeammate.AiTeammateCode.Settings;
using AiTeammate.AiTeammateCode.Localization;

namespace AiTeammate.AiTeammateCode.Solo;

/// <summary>
/// "AI 独自爬塔" mode: a normal singleplayer run whose local player is driven by the AI.
/// The human watches and can interrupt at any time through the chat panel
/// (<see cref="AiFlowControl"/>). Multiplayer teammate mode is not affected.
/// </summary>
public static class SoloRunController
{
    private static readonly Lock Sync = new();
    private static bool _active;
    private static CancellationTokenSource? _loopCts;
    private static bool _runEventHookInstalled;
    private static Rng _fallbackRng = new((uint)DateTime.UtcNow.Ticks);
    private static string _lastBlockReason = "";

    /// <summary>Settings toggle. 单人爬塔模式开关。</summary>
    public static bool IsModeEnabled => AiSettingsStore.Current.EnableSoloClimb;

    /// <summary>True when the AI currently drives the local singleplayer player. 当前是否由 AI 接管。</summary>
    public static bool IsActive
    {
        get
        {
            lock (Sync)
            {
                // Turning the setting off takes effect immediately; the next patch call releases.
                return _active && AiSettingsStore.Current.EnableSoloClimb;
            }
        }
    }

    private static bool IsBound
    {
        get { lock (Sync) return _active; }
    }

    public static bool IsAiControlledPlayer(Player? player)
    {
        if (player == null || !IsActive)
            return false;

        return AiTeammateManager.Instance.AiPlayer?.NetId == player.NetId;
    }

    /// <summary>Hooks <see cref="RunManager.RunStarted"/> once so a fresh run binds immediately.</summary>
    public static void InstallRunHook()
    {
        if (_runEventHookInstalled)
            return;

        var run = RunManager.Instance;
        if (run == null)
            return;

        run.RunStarted += OnRunStarted;
        run.RoomEntered += OnRoomEntered;
        _runEventHookInstalled = true;
    }

    private static void OnRunStarted(RunState state)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized)
            return;

        // A fresh run may reuse the manager with a stale Player reference from the last run.
        if (IsActive)
            Release(manager);

        if (!TryBind(manager))
            RequestBindRetry("RunStarted");
    }

    private static void OnRoomEntered()
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized)
            return;

        TryBind(manager);
    }

    /// <summary>
    /// Retries <see cref="TryBind"/> for a few seconds. A loaded save only gets its local net id when
    /// <c>RunManager.Launch()</c> runs, and the "continue run" path fires several hooks before that,
    /// so a single bind attempt during loading can bail out and leave the run with no AI driver.
    /// </summary>
    public static void RequestBindRetry(string reason)
    {
        if (!IsModeEnabled)
            return;

        var manager = AiTeammateManager.Instance;
        if (!manager.TryInitialize())
            return;

        if (IsBound && manager.IsSoloControlled)
            return;

        // Multiplayer teammate runs never hand control to the AI; only singleplayer can be bound.
        // (An unknown net service is fine — the load path may not have created it yet.)
        var netType = RunManager.Instance?.NetService?.Type;
        if (netType is not (null or NetGameType.Singleplayer))
            return;

        _ = RetryBindAsync(manager, reason);
    }

    private static async Task RetryBindAsync(AiTeammateManager manager, string reason)
    {
        // Covers asset preloading + fade-in on slow machines, then stops.
        int[] delaysMs = [250, 500, 1000, 2000, 3000, 5000];

        foreach (var delayMs in delaysMs)
        {
            try { await Task.Delay(delayMs); } catch { }

            if (!manager.IsInitialized || !IsModeEnabled || manager.IsSoloControlled)
                return;

            bool bound = false;
            try
            {
                Compat.MainThread.Run(() => bound = TryBind(manager));
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"[Solo] rebind after {reason} failed: {ex.Message}");
                return;
            }

            if (bound)
            {
                MainFile.Logger.Info($"[Solo] AI 已恢复接管（{reason}）");
                return;
            }

            // Left the run (or the local player never showed up); stop retrying.
            if (RunManager.Instance?.DebugOnlyGetState() == null)
                return;
        }

        MainFile.Logger.Warn($"[Solo] rebind after {reason} did not succeed; waiting for the next room/map/combat hook");
    }

    /// <summary>
    /// Drives <see cref="TryBind"/> once per second while solo mode is enabled and a singleplayer
    /// run is in progress. Loaded saves can reach a playable state through hooks that fire before
    /// the local player exists; this watchdog guarantees the AI eventually takes over.
    /// 读档后兜底：只要单人模式开着且不在 AI 控制下，就持续重试。
    /// </summary>
    public static void SuperviseTick()
    {
        if (!IsModeEnabled)
            return;

        var manager = AiTeammateManager.Instance;
        if (!manager.TryInitialize())
            return;

        var run = RunManager.Instance;
        if (run?.DebugOnlyGetState() == null)
            return;

        // Multiplayer teammate runs never hand control to the AI.
        if (run.NetService?.Type != NetGameType.Singleplayer)
            return;

        if (IsActive && manager.IsSoloControlled && manager.AiPlayer != null
            && run.DebugOnlyGetState()!.Players.Contains(manager.AiPlayer))
            return;

        TryBind(manager);
    }

    /// <summary>Logs why a bind attempt was refused, once per distinct reason.</summary>
    private static void LogBindBlocked(string reason)
    {
        if (_lastBlockReason == reason)
            return;

        _lastBlockReason = reason;
        MainFile.Logger.Info($"[Solo] bind blocked: {reason}");
    }

    /// <summary>
    /// Binds the local player to AI control when solo mode is on, and releases it when the mode is
    /// switched off or the run ends. Safe to call from any patch. 绑定/释放单人模式控制权。
    /// </summary>
    public static bool TryBind(AiTeammateManager manager)
    {
        if (!manager.TryInitialize())
        {
            LogBindBlocked("manager not initialized");
            return false;
        }

        if (!IsModeEnabled)
        {
            _lastBlockReason = "";
            if (IsBound)
                Release(manager);
            return false;
        }

        var run = RunManager.Instance;
        var netService = run?.NetService;
        if (run == null || netService == null)
        {
            LogBindBlocked("run manager not ready");
            return false;
        }

        if (netService.Type != NetGameType.Singleplayer)
        {
            LogBindBlocked($"net type is {netService.Type}");
            return false;
        }

        var state = run.DebugOnlyGetState();
        if (state == null)
        {
            LogBindBlocked("run state not available yet");
            if (IsBound)
                Release(manager);
            return false;
        }

        if (IsActive && manager.AiPlayer != null && state.Players.Contains(manager.AiPlayer))
        {
            _lastBlockReason = "";
            return true;
        }

        if (IsBound)
        {
            // The bound Player belongs to a previous run.
            Release(manager);
        }

        // Never hijack a multiplayer teammate binding.
        if (manager.AiPlayer != null && !manager.IsSoloControlled)
        {
            LogBindBlocked($"player already bound as teammate ({manager.AiPlayer.Character?.Id.Entry})");
            return false;
        }

        var netId = LocalContext.NetId;
        var me = netId.HasValue ? state.GetPlayer(netId.Value) : null;
        if (me == null)
        {
            // Happens while a save is still loading: RunManager.Launch() has not set the local net
            // id yet (or the saved players are not deserialized). RequestBindRetry covers it.
            LogBindBlocked($"local player not in run state (netId={(netId.HasValue ? netId.Value.ToString() : "unset")}, players={state.Players.Count})");
            return false;
        }

        _lastBlockReason = "";

        lock (Sync)
        {
            _active = true;
            _loopCts?.Cancel();
            _loopCts = new CancellationTokenSource();
        }

        manager.BindSoloPlayer(me);
        AiFlowControl.PausedChanged -= OnPausedChanged;
        AiFlowControl.PausedChanged += OnPausedChanged;

        StartAutomationLoops(_loopCts?.Token ?? CancellationToken.None);
        NGamePatch.RefreshChatUi();
        NGamePatch.UpdateSoloChatControls();

        // If the mode was switched on while the map was already open, pick a route right away.
        if (NMapScreen.Instance is { IsOpen: true })
            SoloMapNavigator.OnMapOpened();

        MainFile.Logger.Info("[Solo] AI 已接管单人爬塔");
        manager.AiLog.Log("SYSTEM", "AI 独自爬塔模式已启动");
        manager.ChatManager.AddSystemMessage(AiLoc.T("system.solo_started",
            "The AI is climbing on its own. Send a message to interrupt it at any time, or use the pause button to make it wait."));
        return true;
    }

    public static void Release(AiTeammateManager manager)
    {
        CancellationTokenSource? cts;
        lock (Sync)
        {
            if (!_active)
                return;

            _active = false;
            cts = _loopCts;
            _loopCts = null;
        }

        try { cts?.Cancel(); } catch { }
        cts?.Dispose();

        AiFlowControl.PausedChanged -= OnPausedChanged;
        AiFlowControl.SetManualPaused(false);
        AiFlowControl.ClearConversationPause();

        manager.ReleaseSoloControl();
        NGamePatch.UpdateSoloChatControls();
        NGamePatch.HideChatPanel();
        MainFile.Logger.Info("[Solo] AI 已释放单人爬塔控制权");
    }

    private static void OnPausedChanged(bool paused)
    {
        MainThread.Post(() =>
        {
            NGamePatch.UpdateSoloChatControls();
            if (!paused)
            {
                ResumeCombatIfNeeded();
                ResumeRoomFlowIfNeeded();
            }
        });
    }

    /// <summary>Re-triggers flows that stopped on a pause and are not waiting on their own.</summary>
    private static void ResumeRoomFlowIfNeeded()
    {
        var manager = AiTeammateManager.Instance;
        var aiPlayer = manager.AiPlayer;
        if (!IsActive || !manager.IsInitialized || aiPlayer == null)
            return;

        try
        {
            if (NMapScreen.Instance is { IsOpen: true })
                SoloMapNavigator.OnMapOpened();

            var state = RunManager.Instance?.DebugOnlyGetState();
            if (state?.CurrentRoom?.RoomType == RoomType.Event)
            {
                var eventSync = RunManager.Instance?.EventSynchronizer;
                if (eventSync != null)
                    EventAutoChoicePatch.Nudge(aiPlayer, eventSync);
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Solo] resume room flow failed: {ex.Message}");
        }
    }

    /// <summary>A paused mid-turn combat holds the turn; restart processing when the player resumes.</summary>
    private static void ResumeCombatIfNeeded()
    {
        var manager = AiTeammateManager.Instance;
        var aiPlayer = manager.AiPlayer;
        if (!IsActive || !manager.IsInitialized || aiPlayer?.Creature == null || !aiPlayer.Creature.IsAlive)
            return;

        if (!CombatManager.Instance.IsInProgress || CombatManager.Instance.IsOverOrEnding)
            return;

        var state = CombatManager.Instance.DebugOnlyGetState();
        if (state?.CurrentSide != CombatSide.Player)
            return;

        if (CombatManager.Instance.IsPlayerReadyToEndTurn(aiPlayer))
            return;

        _ = manager.ProcessCombatTurnAsync();
    }

    // ---- Automation loops ------------------------------------------------------------------

    private static void StartAutomationLoops(CancellationToken ct)
    {
        _ = ProceedLoopAsync(ct);
        _ = OverlayWatchdogAsync(ct);
    }

    /// <summary>
    /// Clicks the room's "proceed" button once the AI has finished with rest sites, events,
    /// shops and treasure rooms. Those rooms otherwise wait for the human to click.
    /// </summary>
    private static async Task ProceedLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(600, ct);

                if (!IsActive)
                {
                    if (IsBound)
                        Release(AiTeammateManager.Instance);
                    break;
                }

                if (RunManager.Instance?.DebugOnlyGetState() == null)
                {
                    Release(AiTeammateManager.Instance);
                    break;
                }

                if (AiFlowControl.IsPaused)
                    continue;

                TryClickProceed();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Solo] proceed loop failed: {ex.Message}");
        }
    }

    private static void TryClickProceed()
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        var room = state?.CurrentRoom;
        if (room == null)
            return;

        if (room.RoomType is not (RoomType.RestSite or RoomType.Event or RoomType.Shop or RoomType.Treasure))
            return;

        // The map is open, which means this room is already finished. Its proceed button stays
        // visible and enabled underneath the map, and every click re-invokes NMapScreen.Open() —
        // which re-triggers SoloMapNavigator and cancels the route decision in flight. In a rest
        // site this loop ran every 600ms and the AI never got to vote, so the run stalled on the map.
        // Nudge the picker instead: it debounces per floor, so this is cheap and also recovers a map
        // whose open hook never fired or whose vote got dropped.
        if (NMapScreen.Instance is { IsOpen: true })
        {
            SoloMapNavigator.OnMapOpened();
            return;
        }

        // Never touch overlay screens (card selections etc.) from here.
        if (NOverlayStack.Instance is { ScreenCount: > 0 })
            return;

        var runNode = NRun.Instance;
        if (runNode == null || !GodotObject.IsInstanceValid(runNode))
            return;

        // A treasure room only enables its proceed button once the chest has been opened and the
        // relic taken; nothing else clicks the chest, so the AI would stall here forever.
        if (room.RoomType == RoomType.Treasure && TryOpenTreasureChest(runNode))
            return;

        // Event rooms end with a special "proceed" option button instead of NProceedButton.
        if (room.RoomType == RoomType.Event)
        {
            var proceedOption = UiHelper.FindAll<NEventOptionButton>(runNode)
                .FirstOrDefault(b => GodotObject.IsInstanceValid(b)
                                     && b.IsVisibleInTree()
                                     && b.Option != null
                                     && !b.Option.IsLocked
                                     && b.Option.IsProceed);
            if (proceedOption != null)
            {
                _ = UiHelper.Click(proceedOption);
                MainFile.Logger.Info("[Solo] clicked event proceed option");
            }
            return;
        }

        // An enabled option button means the AI still has a decision to make.
        if (UiHelper.FindAll<NEventOptionButton>(runNode).Any(b => b.IsVisibleInTree() && b.IsEnabled))
            return;
        if (UiHelper.FindAll<NRestSiteButton>(runNode).Any(b => b.IsVisibleInTree() && b.IsEnabled))
            return;

        var proceed = UiHelper.FindAll<NProceedButton>(runNode)
            .FirstOrDefault(b => b.IsVisibleInTree() && b.IsEnabled);
        if (proceed == null)
            return;

        _ = UiHelper.Click(proceed);
        MainFile.Logger.Info($"[Solo] clicked proceed ({room.RoomType})");
    }

    /// <summary>
    /// Clicks the treasure chest once it becomes clickable. The relic vote itself is cast by
    /// <see cref="TreasureRoomRelicAutoPickPatch"/> when the relic collection screen is initialized.
    /// </summary>
    private static bool TryOpenTreasureChest(Node runNode)
    {
        var treasureRoom = UiHelper.FindAll<NTreasureRoom>(runNode)
            .FirstOrDefault(r => GodotObject.IsInstanceValid(r) && r.IsVisibleInTree());
        if (treasureRoom == null)
            return false;

        // Same lookup as the game's own AutoSlay treasure handler; works on both game versions.
        var chest = treasureRoom.GetNodeOrNull<NClickableControl>("Chest");
        if (chest == null || !GodotObject.IsInstanceValid(chest) || !chest.IsVisibleInTree() || !chest.IsEnabled)
            return false;

        _ = UiHelper.Click(chest);
        MainFile.Logger.Info("[Solo] opening treasure chest");
        return true;
    }

    /// <summary>
    /// Safety valve for screens we do not drive ourselves: after a while, hand the screen to the
    /// game's built-in AutoSlay handler; if there is none, pause the AI and ask the human.
    /// </summary>
    private static async Task OverlayWatchdogAsync(CancellationToken ct)
    {
        object? watched = null;
        var watchedSince = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(700, ct);

                if (!IsActive)
                {
                    if (IsBound)
                        Release(AiTeammateManager.Instance);
                    break;
                }
                if (AiFlowControl.IsPaused)
                {
                    watched = null;
                    continue;
                }

                var overlay = NOverlayStack.Instance?.Peek();
                if (overlay == null)
                {
                    watched = null;
                    continue;
                }

                if (!ReferenceEquals(watched, overlay))
                {
                    watched = overlay;
                    watchedSince = DateTime.UtcNow;
                    continue;
                }

                if ((DateTime.UtcNow - watchedSince).TotalSeconds < 6)
                    continue;

                if (await TryRunFallbackHandlerAsync(overlay, ct))
                {
                    watched = null;
                    continue;
                }

                MainFile.Logger.Warn($"[Solo] unhandled screen {overlay.GetType().Name}, pausing AI");
                AiFlowControl.SetManualPaused(true);
                AiTeammateManager.Instance.ChatManager.AddSystemMessage(
                    AiLoc.T("system.solo_unhandled_screen",
                        "The AI hit a screen it can't handle automatically ({Screen}) and has paused. Handle it manually, then press Resume.",
                        ("Screen", overlay.GetType().Name)));
                watched = null;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Solo] overlay watchdog failed: {ex.Message}");
        }
    }

    private static async Task<bool> TryRunFallbackHandlerAsync(object overlay, CancellationToken ct)
    {
        IScreenHandler? handler = overlay switch
        {
            NRewardsScreen => new RewardsScreenHandler(),
            NCardRewardSelectionScreen => new CardRewardScreenHandler(),
            NDeckUpgradeSelectScreen => new DeckUpgradeScreenHandler(),
            NDeckTransformSelectScreen => new DeckTransformScreenHandler(),
            NDeckEnchantSelectScreen => new DeckEnchantScreenHandler(),
            NDeckCardSelectScreen => new DeckCardSelectScreenHandler(),
            NSimpleCardSelectScreen => new SimpleCardSelectScreenHandler(),
            NChooseACardSelectionScreen => new ChooseACardScreenHandler(),
            NChooseABundleSelectionScreen => new ChooseABundleScreenHandler(),
            NChooseARelicSelection => new ChooseARelicScreenHandler(),
            NCrystalSphereScreen => new CrystalSphereScreenHandler(),
            _ => null
        };

        if (handler == null)
            return false;

        try
        {
            MainFile.Logger.Info($"[Solo] fallback handler for {overlay.GetType().Name}");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await handler.HandleAsync(_fallbackRng, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Solo] fallback handler {handler.GetType().Name} failed: {ex.Message}");
            return false;
        }
    }

    // ---- New run ---------------------------------------------------------------------------

    public static bool CanStartNewRun => RunManager.Instance?.DebugOnlyGetState() == null;

    public static int MaxAscensionLevel
    {
        get
        {
            try { return MegaCrit.Sts2.Core.Entities.Ascension.AscensionManager.maxAscensionAllowed; }
            catch { return 0; }
        }
    }

    /// <summary>Character choices for the external agent, without the synthetic "random" entry.</summary>
    public static IReadOnlyList<(string Id, string Name)> GetCharacterChoices()
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var character in AiTeammateManager.Instance.GetAiCharacterOptions())
                list.Add((character.Id.Entry, PromptTextHelper.ResolveLocString(character.Title, character.Id.Entry)));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Solo] character list failed: {ex.Message}");
        }

        return list;
    }

    /// <summary>
    /// Starts a brand-new singleplayer run for the external agent. Solo climb is forced off so the
    /// built-in driver never competes with dsh for the same player.
    /// </summary>
    public static (bool Ok, string Message) StartAgentRun(string characterId, int ascensionLevel, string? seed)
    {
        var game = NGame.Instance;
        if (game == null)
            return (false, "The game isn't ready yet.");
        if (!CanStartNewRun)
            return (false, "A run is already in progress; finish or abandon it first.");

        try
        {
            var manager = AiTeammateManager.Instance;
            if (!manager.IsInitialized)
                manager.Initialize();

            var choices = manager.GetAiCharacterOptions();
            CharacterModel? character = null;
            if (!string.IsNullOrWhiteSpace(characterId))
            {
                character = choices.FirstOrDefault(c =>
                                string.Equals(c.Id.Entry, characterId, StringComparison.OrdinalIgnoreCase))
                            ?? choices.FirstOrDefault(c =>
                                c.Id.Entry.Contains(characterId, StringComparison.OrdinalIgnoreCase));
            }

            character ??= choices.Count == 0 ? null : choices[System.Random.Shared.Next(choices.Count)];
            if (character == null)
                return (false, "No available character was found.");

            var ascension = Math.Clamp(ascensionLevel, 0, MaxAscensionLevel);
            seed = string.IsNullOrWhiteSpace(seed)
                ? Guid.NewGuid().ToString("N")[..10]
                : seed.Trim();

            var unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();
            var rng = new Rng((uint)StringHelper.GetDeterministicHashCode(seed), "act_selection");
            var acts = ActModel.GetRandomList(rng, unlockState, isMultiplayer: false).ToList();
            if (acts.Count == 0)
                return (false, "No acts are available.");

            if (AiSettingsStore.Current.EnableSoloClimb)
            {
                AiSettingsStore.Current.EnableSoloClimb = false;
                AiSettingsStore.Save();
            }

            var characterName = PromptTextHelper.ResolveLocString(character.Title, character.Id.Entry);
            MainFile.Logger.Info($"[Agent] starting run for dsh: char={character.Id.Entry} seed={seed} " +
                                 $"acts={acts.Count} ascension={ascension}");
            _ = RunAndLogAsync(game, character, seed, acts, unlockState, ascension);
            return (true, $"Starting a run with {characterName} (ascension {ascension}, seed {seed}).");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Agent] start run failed: {ex}");
            return (false, "Failed to start: " + ex.Message);
        }
    }

    /// <summary>Starts a brand-new singleplayer run that the AI will immediately take over.</summary>
    public static Task<(bool Ok, string Message)> StartNewSoloRunAsync()
    {
        var game = NGame.Instance;
        if (game == null)
            return Task.FromResult((false, AiLoc.T("solo.not_ready", "The game isn't ready yet.")));
        if (!CanStartNewRun)
            return Task.FromResult((false, AiLoc.T("solo.run_in_progress",
                "A run is already in progress; finish or abandon it first.")));

        try
        {
            var manager = AiTeammateManager.Instance;
            if (!manager.IsInitialized)
                manager.Initialize();

            var character = ResolveSoloCharacter(manager);
            if (character == null)
                return Task.FromResult((false, AiLoc.T("solo.no_character", "No available character was found.")));

            var seed = Guid.NewGuid().ToString("N")[..10];
            var unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();
            var rng = new Rng((uint)StringHelper.GetDeterministicHashCode(seed), "act_selection");
            var acts = ActModel.GetRandomList(rng, unlockState, isMultiplayer: false).ToList();
            if (acts.Count == 0)
                return Task.FromResult((false, AiLoc.T("solo.no_acts", "No acts are available.")));

            var ascension = Math.Clamp(AiSettingsStore.Current.SoloAscensionLevel, 0,
                MegaCrit.Sts2.Core.Entities.Ascension.AscensionManager.maxAscensionAllowed);

            AiSettingsStore.Current.EnableSoloClimb = true;
            AiSettingsStore.Save();

            MainFile.Logger.Info($"[Solo] starting AI run: char={character.Id.Entry} seed={seed} acts={acts.Count} ascension={ascension}");
            _ = RunAndLogAsync(game, character, seed, acts, unlockState, ascension);
            return Task.FromResult((true, AiLoc.T("solo.starting", "Starting a solo AI climb ({Character}).",
                ("Character", PromptTextHelper.ResolveLocString(character.Title, character.Id.Entry)))));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Solo] start run failed: {ex}");
            return Task.FromResult((false, AiLoc.T("solo.start_failed", "Failed to start: {Message}",
                ("Message", ex.Message))));
        }
    }

    private static async Task RunAndLogAsync(NGame game, CharacterModel character, string seed,
        List<ActModel> acts, UnlockState unlockState, int ascensionLevel)
    {
        try
        {
            await GameCompat.StartNewSingleplayerRunAsync(game, character, acts, Array.Empty<ModifierModel>(), seed,
                GameMode.Standard, isCustom: false, ascensionLevel, dailyTime: null);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Solo] StartNewSingleplayerRun failed: {ex}");
            try
            {
                AiTeammateManager.Instance.ChatManager.AddSystemMessage(
                    AiLoc.T("system.solo_start_failed", "Failed to start the AI climb: {Message}",
                        ("Message", ex.Message)));
            }
            catch { }
        }
    }

    private static CharacterModel? ResolveSoloCharacter(AiTeammateManager manager)
    {
        var options = manager.GetAiCharacterOptions();
        var id = AiSettingsStore.Current.SoloCharacterId;
        if (!string.IsNullOrWhiteSpace(id))
        {
            var chosen = options.FirstOrDefault(c => c.Id.Entry == id);
            if (chosen != null)
                return chosen;
        }

        return options.Count == 0 ? null : options[System.Random.Shared.Next(options.Count)];
    }

    /// <summary>Character choices for the settings dropdown ("随机" first).</summary>
    public static IReadOnlyList<(string Id, string Name)> GetSoloCharacterOptions()
    {
        var list = new List<(string, string)> { ("", AiLoc.T("solo.random", "Random")) };
        try
        {
            // No manager.Initialize() here: this provider also runs while the mod settings page is
            // registered, before ModelDb exists. GetAiCharacterOptions() itself only reads ModelDb.
            foreach (var character in AiTeammateManager.Instance.GetAiCharacterOptions())
                list.Add((character.Id.Entry, PromptTextHelper.ResolveLocString(character.Title, character.Id.Entry)));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Solo] character list failed: {ex.Message}");
        }

        return list;
    }
}
