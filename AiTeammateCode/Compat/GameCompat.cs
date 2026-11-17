using System;
using System.Collections;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;

namespace AiTeammate.AiTeammateCode.Compat;

/// <summary>
/// Small shims for the game API differences between 0.107.1 (stable) and 0.111.0 (beta).
/// Everything that needs a per-version branch lives here so the rest of the mod is
/// version-agnostic.
/// </summary>
internal static class GameCompat
{
    /// <summary>0.111 made StartRunLobby.MaxPlayers a private field.</summary>
    public static int MaxPlayers(StartRunLobby lobby)
    {
#if STS2_API_0_111_OR_LATER
        return AccessTools.Field(typeof(StartRunLobby), "_maxPlayers")?.GetValue(lobby) is int max ? max : 4;
#else
        return lobby.MaxPlayers;
#endif
    }

    /// <summary>0.111 replaced RunLobby.ConnectedPlayerIds with the Players/PlayerIds pair.</summary>
    public static IEnumerable<ulong> ConnectedPlayerIds(RunLobby lobby)
    {
#if STS2_API_0_111_OR_LATER
        return lobby.PlayerIds;
#else
        return lobby.ConnectedPlayerIds;
#endif
    }

    /// <summary>0.111 added the chooser parameter to SignalPlayerChoiceBegun.</summary>
    public static Task SignalPlayerChoiceBegun(PlayerChoiceContext context, Player chooser, PlayerChoiceOptions options)
    {
#if STS2_API_0_111_OR_LATER
        return context.SignalPlayerChoiceBegun(chooser, options);
#else
        return context.SignalPlayerChoiceBegun(options);
#endif
    }

    /// <summary>0.111 made the act index part of the vote action so stale votes can be ignored.</summary>
    public static GameAction CreateVoteToMoveToNextAct(Player player)
    {
#if STS2_API_0_111_OR_LATER
        return new VoteToMoveToNextActAction(player, player.RunState?.CurrentActIndex ?? 0);
#else
        return new VoteToMoveToNextActAction(player);
#endif
    }

    /// <summary>
    /// Marks the AI player as connected/ready in a resumed-run lobby. In 0.107 the lobby tracks
    /// connection and readiness in two sets; 0.111 folds both into the player list entry.
    /// </summary>
    public static void MarkLoadLobbyPlayerConnected(LoadRunLobby lobby, ulong netId)
    {
#if STS2_API_0_111_OR_LATER
        // LoadRunLobbyPlayer is a struct, so add a new entry or update the existing one in place.
        var index = lobby.Players.FindIndex(p => p.id == netId);
        MegaCrit.Sts2.Core.Entities.Multiplayer.LoadRunLobbyPlayer player;
        if (index < 0)
        {
            player = new MegaCrit.Sts2.Core.Entities.Multiplayer.LoadRunLobbyPlayer
            {
                id = netId,
                isModded = true,
                isReady = true
            };
            lobby.Players.Add(player);
        }
        else
        {
            player = lobby.Players[index];
            player.isReady = true;
            lobby.Players[index] = player;
        }

        // The game always hands the listener the same entry that lives in Players.
        lobby.LobbyListener.PlayerConnected(player);
#else
        if (lobby.ConnectedPlayerIds.Add(netId))
            lobby.LobbyListener.PlayerConnected(netId);

        var readyPlayers = (HashSet<ulong>?)AccessTools.Field(typeof(LoadRunLobby), "_readyPlayers")?.GetValue(lobby);
        if (readyPlayers != null && readyPlayers.Add(netId))
            lobby.LobbyListener.PlayerReadyChanged(netId);
#endif
    }

    /// <summary>
    /// Completes the resting AI player's rest-site slot in <see cref="RestSiteSynchronizer"/>.
    ///
    /// 0.111 made <c>RestSiteRoom.Exit</c> await <c>RestSiteSynchronizer.AfterAllRestSitesCompleted()</c>,
    /// which awaits every <c>PlayerRestSite.completionTaskSource</c>. A player's slot is normally
    /// completed by its own OptionIndexChosen / RestSiteSkipped message, but an AI teammate has no
    /// net peer, so nothing ever arrives for it: the room exit never resumes and the next map travel
    /// parks on the fade-out (a black screen). 0.107 has no completion source — this is a no-op there.
    /// </summary>
    public static void CompleteAiRestSiteSlot(RestSiteSynchronizer sync, Player player)
    {
        try
        {
            var syncType = typeof(RestSiteSynchronizer);
            if (AccessTools.Field(syncType, "_restSites")?.GetValue(sync) is not IList restSites)
                return;
            if (AccessTools.Field(syncType, "_playerCollection")?.GetValue(sync) is not IPlayerCollection collection)
                return;

            var slot = collection.GetPlayerSlotIndex(player);
            if (slot < 0 || slot >= restSites.Count)
                return;

            CompleteRestSiteSlot(restSites[slot]);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[REST] CompleteAiRestSiteSlot failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears a single <c>PlayerRestSite</c>'s remaining options and completes its task source.
    /// Takes <see cref="object"/> because <c>PlayerRestSite</c> is a private nested type.
    /// </summary>
    public static void CompleteRestSiteSlot(object? playerRestSite)
    {
        if (playerRestSite == null)
            return;

        var type = playerRestSite.GetType();

        // Vanilla clears whatever is left when a player's rest site ends; the AI never picks twice.
        if (AccessTools.Field(type, "options")?.GetValue(playerRestSite) is IList options)
            options.Clear();

        if (AccessTools.Field(type, "completionTaskSource")?.GetValue(playerRestSite) is TaskCompletionSource completion)
            completion.TrySetResult();
    }

    // ---- 0.113 shims -------------------------------------------------------------------

    /// <summary>0.113 folded the custom-run game mode into the isCustom flag on the run/lobby.</summary>
    public static GameMode CustomRunGameMode
    {
        get
        {
#if STS2_API_0_113_OR_LATER
            return GameMode.Standard;
#else
            return GameMode.Custom;
#endif
        }
    }

    /// <summary>0.113 renamed <c>UndoReadyToEndTurn</c> to <c>TryUndoReadyToEndTurn</c>.</summary>
    public static void UndoReadyToEndTurn(CombatManager combat, Player player)
    {
#if STS2_API_0_113_OR_LATER
        combat.TryUndoReadyToEndTurn(player);
#else
        combat.UndoReadyToEndTurn(player);
#endif
    }

    /// <summary>0.113 removed <c>SmithRestSiteOption.SmithCount</c>; a smith always upgrades exactly one card.</summary>
    public static int SmithCount(SmithRestSiteOption option)
    {
#if STS2_API_0_113_OR_LATER
        _ = option;
        return 1;
#else
        return option.SmithCount;
#endif
    }

    /// <summary>
    /// 0.113 starts a singleplayer run from a <see cref="Player"/> (built here with the singleplayer net id) and
    /// marks custom runs with an <c>isCustomGame</c> flag instead of a <c>GameMode.Custom</c>.
    /// </summary>
    public static Task StartNewSingleplayerRunAsync(NGame game, CharacterModel character,
        IReadOnlyList<ActModel> acts, IReadOnlyList<ModifierModel> modifiers, string seed,
        GameMode gameMode, bool isCustom, int ascensionLevel, DateTimeOffset? dailyTime)
    {
#if STS2_API_0_113_OR_LATER
        var player = Player.CreateForNewRun(character,
            SaveManager.Instance.GenerateUnlockStateFromProgress(), SingleplayerNetId);
        return game.StartNewSingleplayerRun(player, shouldSave: true, acts, modifiers, seed, gameMode,
            isCustomGame: isCustom, ascensionLevel: ascensionLevel, dailyTime: dailyTime);
#else
        _ = isCustom;
        return game.StartNewSingleplayerRun(character, shouldSave: true, acts, modifiers, seed, gameMode,
            ascensionLevel: ascensionLevel, dailyTime: dailyTime);
#endif
    }

    /// <summary>Local net id of a plain singleplayer session (<c>NetSingleplayerGameService.NetId</c>).</summary>
    private const ulong SingleplayerNetId = 1UL;

    /// <summary>0.113 renamed <c>MerchantEntry.OnTryPurchaseWrapper</c> to <c>OnTryPurchaseUnsynchronized</c>.</summary>
    public static Task<bool> PurchaseEntry(MerchantEntry entry, MerchantInventory inventory)
    {
#if STS2_API_0_113_OR_LATER
        return entry.OnTryPurchaseUnsynchronized(inventory, ignoreCost: false);
#else
        return entry.OnTryPurchaseWrapper(inventory);
#endif
    }

    /// <summary>
    /// 0.113 turned <c>ProceedFromTerminalRewardsScreen</c> into a fire-and-forget wrapper; the awaitable twin
    /// carries an <c>Async</c> suffix.
    /// </summary>
    public static Task ProceedFromTerminalRewardsAsync(RunManager run)
    {
#if STS2_API_0_113_OR_LATER
        return run.ProceedFromTerminalRewardsScreenAsync();
#else
        return run.ProceedFromTerminalRewardsScreen();
#endif
    }

    /// <summary>0.113 dropped the <c>isRandomCharacterResolution</c> parameter from every lobby screen's PlayerChanged.</summary>
    public static void NotifyPlayerChanged(NCharacterSelectScreen screen, LobbyPlayer player)
    {
#if STS2_API_0_113_OR_LATER
        screen.PlayerChanged(player);
#else
        screen.PlayerChanged(player, isRandomCharacterResolution: false);
#endif
    }

    /// <inheritdoc cref="NotifyPlayerChanged(NCharacterSelectScreen, LobbyPlayer)"/>
    public static void NotifyPlayerChanged(NDailyRunScreen screen, LobbyPlayer player)
    {
#if STS2_API_0_113_OR_LATER
        screen.PlayerChanged(player);
#else
        screen.PlayerChanged(player, isRandomCharacterResolution: false);
#endif
    }

    /// <inheritdoc cref="NotifyPlayerChanged(NCharacterSelectScreen, LobbyPlayer)"/>
    public static void NotifyPlayerChanged(NCustomRunScreen screen, LobbyPlayer player)
    {
#if STS2_API_0_113_OR_LATER
        screen.PlayerChanged(player);
#else
        screen.PlayerChanged(player, isRandomCharacterResolution: false);
#endif
    }

    /// <summary>0.113 split the lobby player's single multiplayer ascension cap into standard/blitz caps.</summary>
    public static LobbyPlayer CreateLobbyPlayer(ulong id, int slot, CharacterModel character, LobbyPlayer host, bool isReady)
    {
        return new LobbyPlayer
        {
            id = id,
            slotId = slot,
            character = character,
            unlockState = host.unlockState,
#if STS2_API_0_113_OR_LATER
            maxStandardAscension = host.maxStandardAscension,
            maxBlitzAscension = host.maxBlitzAscension,
            isModded = true,
#elif STS2_API_0_111_OR_LATER
            maxMultiplayerAscensionUnlocked = host.maxMultiplayerAscensionUnlocked,
            isModded = true,
#else
            maxMultiplayerAscensionUnlocked = host.maxMultiplayerAscensionUnlocked,
#endif
            isReady = isReady
        };
    }

    /// <summary>
    /// True for every lobby kind without a vanilla "add AI teammate" control: daily, custom and
    /// (0.113.1+) Dread and Blitz. A standard lobby keeps the character-select screen's own AI panel.
    ///
    /// Deliberately written as "anything but Standard": <c>GameMode.Dread</c>/<c>Blitz</c> were
    /// appended to the enum in 0.113.1, so naming them here would not compile against the older
    /// snapshots this bundle still ships (0.107.1 / 0.111.x / 0.113.0), while <c>GameMode.Standard</c>
    /// exists in every version. 0.113 also turned a custom run into "Standard + the IsCustom flag",
    /// which the flag check below picks up.
    /// 非标准局（每日 / 自定 / Dread / Blitz）的大厅都没有原生「添加 AI」入口。
    /// </summary>
    public static bool IsAutoAiLobby(StartRunLobby lobby)
    {
#if STS2_API_0_113_OR_LATER
        return lobby.GameMode != GameMode.Standard || lobby.IsCustom;
#else
        return lobby.GameMode != GameMode.Standard;
#endif
    }

    /// <summary>
    /// Resolves a game-mode name at runtime. The value is parsed from the *host* game's enum, so
    /// "Dread"/"Blitz" resolve on 0.113.1+ even though the content assembly was compiled against
    /// 0.113.0 (which has neither member); on an older host the parse simply fails and returns null.
    /// 按名字取游戏模式；老版本上没有 Dread/Blitz 时返回 null。
    /// </summary>
    public static GameMode? GameModeByName(string name)
    {
        try
        {
            if (Enum.TryParse<GameMode>(name, ignoreCase: true, out var mode)
                && Enum.IsDefined(typeof(GameMode), mode))
            {
                return mode;
            }
        }
        catch
        {
            // A name the host enum does not have is the expected miss, not an error.
        }

        return null;
    }

    /// <summary>
    /// Re-delivers a lobby player change to a screen whose concrete type only exists on 0.113.1+
    /// (Dread / Blitz). Those lobby screens implement <c>IStartRunLobbyListener.PlayerChanged(LobbyPlayer)</c>
    /// exactly like the daily/custom screens, but naming them would break the older variants, so the
    /// call goes through reflection.
    /// </summary>
    public static void NotifyPlayerChanged(NSubmenu screen, LobbyPlayer player)
    {
        try
        {
            var method = screen.GetType().GetMethod("PlayerChanged", new[] { typeof(LobbyPlayer) });
            method?.Invoke(screen, new object[] { player });
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Lobby] could not refresh the player list of {screen.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>0.113 added the <c>isCustom</c> flag to <c>NMultiplayerSubmenu.FastHost</c>.</summary>
    public static void FastHost(NMultiplayerSubmenu submenu, GameMode gameMode, bool isCustom)
    {
#if STS2_API_0_113_OR_LATER
        submenu.FastHost(gameMode, isCustom);
#else
        _ = isCustom;
        submenu.FastHost(gameMode);
#endif
    }
}
