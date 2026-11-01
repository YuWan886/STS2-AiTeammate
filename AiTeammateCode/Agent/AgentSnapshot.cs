using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Decision;
using AiTeammate.AiTeammateCode.Solo;

namespace AiTeammate.AiTeammateCode.Agent;

/// <summary>
/// One immutable view of "what is the game asking for right now". Built on the Godot main thread;
/// the bridge thread only reads <see cref="Text"/> / <see cref="Token"/>.
/// 外部 Agent 的状态快照。
/// </summary>
internal sealed class AgentSnapshot
{
    public JsonObject Json { get; init; } = new();

    public string Token { get; init; } = "";

    public string Phase { get; init; } = "idle";

    public bool Actionable { get; init; }

    public string BlockedBy { get; init; } = "";

    /// <summary>True when the AI player (not the local human) is the one dsh drives. AI 玩家模式。</summary>
    public bool AiControlled { get; init; }

    public string Text => Json.ToJsonString(AgentJson.Options);
}

internal static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

/// <summary>
/// Converts live game state into the JSON snapshot consumed by the dsh plugin. Everything here must
/// run on the main thread. Option enumeration is shared with <see cref="AgentActions"/> so the index
/// dsh saw is the index the action resolves.
/// </summary>
internal static class AgentSnapshotBuilder
{
    private sealed class Context
    {
        public readonly JsonObject Json = new() { ["schema"] = 1 };
        public readonly JsonArray Options = new();
        public readonly JsonArray Notes = new();
        public string Phase = "idle";
        public bool Actionable;
        public string BlockedBy = "";
        public Player? Local;
        public Player? AiPlayer;
        public RunState? Run;
        public bool Singleplayer;
        public bool AiControlled;
        public bool ActionsDisabled;
    }

    public static AgentSnapshot Capture()
    {
        var ctx = new Context();
        try
        {
            Fill(ctx);
        }
        catch (Exception ex)
        {
            ctx.Phase = "idle";
            ctx.Actionable = false;
            ctx.BlockedBy = "capture_error";
            ctx.Notes.Add("State capture failed: " + ex.Message);
            MainFile.Logger.Warn($"[Agent] snapshot failed: {ex.Message}");
        }

        var json = ctx.Json;
        json["phase"] = ctx.Phase;
        json["actionable"] = ctx.Actionable;
        json["blocked_by"] = ctx.BlockedBy;
        json["mode"] = RunManager.Instance?.NetService?.Type.ToString() ?? "None";
        json["options"] = ctx.Options;
        json["notes"] = ctx.Notes;

        // Token = hash of everything except the token itself. Any observable change flips it, so
        // sts2_wait can tell "the animation finished / the hand changed" apart from "still waiting".
        var tokenSource = json.ToJsonString(AgentJson.Options);
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokenSource)))[..8].ToLowerInvariant();
        json["token"] = token;

        return new AgentSnapshot
        {
            Json = json,
            Token = token,
            Phase = ctx.Phase,
            Actionable = ctx.Actionable,
            BlockedBy = ctx.BlockedBy,
            AiControlled = ctx.AiControlled
        };
    }

    // ---- Top-level flow -------------------------------------------------------------------

    private static void Fill(Context ctx)
    {
        var run = RunManager.Instance;
        var state = run?.DebugOnlyGetState();
        ctx.Run = state;

        // dsh can see (and change, via sts2_set_nickname) how the AI teammate is displayed.
        ctx.Json["ai_name"] = AiNames.DisplayName;
        ctx.Json["ai_nickname"] = AiNames.ConfiguredNickname;

        // Chat relay state: pending lines carry the seq sts2_chat_send wants, so an autoplay loop
        // that spots the chat here can answer it before the plugin's injected block arrives.
        ctx.Json["chat"] = AgentChatRelay.SnapshotJson();
        if (AgentChatRelay.UnreadCount > 0)
        {
            ctx.Notes.Add("The player wrote in the in-game chat. See chat.pending for the seq and "
                          + "answer with sts2_chat_send {text, reply_to: <seq>}.");
        }

        if (state == null)
        {
            DetectMenu(ctx);
            return;
        }

        ctx.Local = LocalContext.NetId is ulong localId ? state.GetPlayer(localId) : null;
        ctx.Singleplayer = RunManager.Instance?.NetService?.Type == NetGameType.Singleplayer;
        var manager = AiTeammateManager.Instance;
        ctx.ActionsDisabled = manager == null || !manager.IsInitialized || !AgentBridgeService.AllowActions;

        // When the mod drives an AI player (solo climb or a multiplayer AI teammate) and dsh is
        // attached, dsh becomes that AI's brain: the mod keeps the hands, dsh supplies decisions.
        var bound = manager?.AiPlayer;
        ctx.AiPlayer = bound != null && state.Players.Contains(bound) ? bound : null;

        FillRunSection(ctx, state);

        // The pause menu is a capstone submenu, not an overlay: without this check the snapshot would
        // report the room underneath as actionable while the player is paused.
        // 暂停菜单不在 NOverlayStack 里，必须单独识别。
        if (AgentActions.IsPauseMenuOpen())
        {
            ctx.Phase = "paused";
            ctx.Actionable = false;
            ctx.BlockedBy = "paused";
            ctx.Json["controlled"] = new JsonObject
            {
                ["kind"] = ctx.AiPlayer != null && AgentDecisionBroker.Enabled ? "ai" : "local"
            };
            ctx.Notes.Add("The game is paused. Call sts2_resume to continue, "
                          + "or sts2_save / sts2_quit_to_menu / sts2_abandon.");
            return;
        }

        if (ctx.AiPlayer != null && AgentDecisionBroker.Enabled)
        {
            FillAiDecision(ctx, ctx.AiPlayer);
            return;
        }

        if (ctx.Local == null)
        {
            ctx.Phase = "idle";
            ctx.BlockedBy = "no_local_player";
            ctx.Notes.Add("The local player is not in the run state yet (loading?). Call sts2_state again in a moment.");
            return;
        }

        ctx.Json["controlled"] = new JsonObject
        {
            ["kind"] = "local",
            ["name"] = SafeLoc(ctx.Local.Character?.Title, ctx.Local.Character?.Id.Entry ?? "player"),
            ["net_id"] = ctx.Local.NetId
        };

        // dsh can park decisions for the local player too (kind=hand_select replaces the vanilla hand
        // UI); while one is pending it is the only thing dsh should answer. 本地玩家也可能有挂起决策。
        var localDecision = AgentDecisionBroker.SnapshotJson();
        if (localDecision != null)
        {
            ctx.Phase = "ai_decision";
            ctx.Actionable = true;
            ctx.Json["decision"] = localDecision;
            AppendCombatJson(ctx, ctx.Local, CombatManager.Instance.DebugOnlyGetState(), withOptions: false);
            ctx.Notes.Add("The game is waiting for your "
                          + (localDecision["kind"]?.GetValue<string>() ?? "card")
                          + " decision. Read decision.prompt / decision.options and answer with "
                          + "sts2_decide {decision: {...}} — use exactly the JSON shape the prompt asks for.");
            return;
        }

        DetectPhase(ctx);

        if (ctx.Actionable && ctx.ActionsDisabled)
        {
            ctx.Actionable = false;
            ctx.BlockedBy = "actions_disabled";
            ctx.Notes.Add("Action tools are disabled in the mod settings.");
        }
        else if (ctx.Actionable && !ctx.Singleplayer)
        {
            ctx.Notes.Add("Multiplayer: you drive the local player only; other players still take their own turns and votes.");
        }

        if (ctx.Actionable)
            AppendNextStepHint(ctx);
    }

    /// <summary>
    /// The AI player is in dsh's hands: either a decision is parked (answer it) or the AI is busy
    /// executing the previous one. 对接 dsh 后，AI 的决策交给 dsh。
    /// </summary>
    private static void FillAiDecision(Context ctx, Player aiPlayer)
    {
        ctx.AiControlled = true;
        ctx.Json["controlled"] = new JsonObject
        {
            ["kind"] = "ai",
            ["name"] = SafeLoc(aiPlayer.Character?.Title, aiPlayer.Character?.Id.Entry ?? "AI"),
            ["character_id"] = aiPlayer.Character?.Id.Entry ?? "",
            ["net_id"] = aiPlayer.NetId,
            ["solo_climb"] = SoloRunController.IsActive
        };

        // The live board in both branches. The hand changes while the executor plays (cards and
        // potions draw), so dsh can re-read it here instead of waiting for the next decision prompt
        // to show the new cards. 实时手牌：抽到的牌在下次决策前也能看到。
        AppendCombatJson(ctx, aiPlayer, CombatManager.Instance.DebugOnlyGetState(), withOptions: false);

        var decision = AgentDecisionBroker.SnapshotJson();
        if (decision != null)
        {
            ctx.Phase = "ai_decision";
            ctx.Actionable = true;
            ctx.Json["decision"] = decision;
            ctx.Notes.Add(
                $"The AI is waiting for your {decision["kind"]} decision. Read decision.prompt and answer with "
                + "sts2_decide {decision: {...}} — use exactly the JSON shape that prompt asks for.");
            return;
        }

        ctx.Phase = "ai_busy";
        ctx.Actionable = false;
        ctx.BlockedBy = "ai_busy";
        ctx.Notes.Add("The AI is executing the previous decision (playing cards / resolving the room). "
                      + "Call sts2_wait for the next decision; sts2_state's combat section shows the live hand.");
    }

    /// <summary>
    /// No run is in progress: expose the main menu itself (root buttons, submenus and lobbies) so
    /// the agent can press what a human would, instead of reporting a hardcoded menu.
    /// 主菜单：根按钮 / 子菜单 / 大厅都暴露给 dsh；开局/续档仍是控制工具。
    /// </summary>
    private static void DetectMenu(Context ctx)
    {
        if (NGame.Instance == null)
        {
            ctx.Phase = "idle";
            ctx.Actionable = false;
            ctx.BlockedBy = "loading";
            ctx.Notes.Add("The game is still loading. Call sts2_wait.");
            return;
        }

        if (!AgentMenuSupport.TryCapture(out var menu))
        {
            ctx.Phase = "idle";
            ctx.Actionable = false;
            ctx.BlockedBy = "loading";
            ctx.Notes.Add("The main menu is not open yet. Call sts2_wait.");
            return;
        }

        ctx.Phase = menu.Phase;
        ctx.Actionable = menu.Actionable;
        ctx.BlockedBy = menu.BlockedBy;
        ctx.Json["menu"] = menu.Menu;
        foreach (var option in menu.Options)
            ctx.Options.Add(option.ToJson());
        foreach (var note in menu.Notes)
            ctx.Notes.Add(note);
    }

    private static void FillRunSection(Context ctx, RunState state)
    {
        var p = ctx.Local;
        var coord = state.CurrentMapCoord;
        var run = new JsonObject
        {
            ["act"] = state.CurrentActIndex + 1,
            ["act_name"] = SafeLoc(state.Act?.Title, state.Act?.Id.Entry ?? "?"),
            ["floor"] = state.TotalFloor,
            ["ascension"] = state.AscensionLevel,
            ["room"] = state.CurrentRoom?.RoomType.ToString() ?? "None",
            ["map_row"] = coord?.row ?? -1,
            ["map_col"] = coord?.col ?? -1
        };

        if (p != null)
        {
            run["character"] = SafeLoc(p.Character?.Title, p.Character?.Id.Entry ?? "?");
            run["character_id"] = p.Character?.Id.Entry ?? "";
            run["hp"] = p.Creature?.CurrentHp ?? 0;
            run["max_hp"] = p.Creature?.MaxHp ?? 0;
            run["gold"] = p.Gold;
            run["deck_size"] = p.Deck.Cards.Count;
            run["relics"] = Names(p.Relics.Select(r => (SafeLoc(r.Title, r.Id.Entry), r.Id.Entry)));
            run["potions"] = PotionsJson(p.Potions);
        }

        ctx.Json["run"] = run;
    }

    // ---- Phase detection ------------------------------------------------------------------

    private static void DetectPhase(Context ctx)
    {
        // A target picker disables whatever room or screen is underneath and waits for one node
        // click — the action that opened it (MEND, some potions/relics) is still pending. It must
        // win over the room detection, or dsh would re-click that action instead of the target.
        // 选目标优先于房间：否则 dsh 会重复点击打开选择器的那一项。
        if (DetectTargetSelect(ctx))
            return;

        // The map opens on top of the finished room's overlay: a terminal rewards screen stays on the
        // overlay stack (RunManager.ClearScreens only clears it on the room transition) and the map's
        // Opened signal merely runs AfterOverlayHidden(), which disables that screen's proceed button.
        // Reading the overlay top blindly then reports rewards_waiting forever while the game is
        // actually waiting for a map pick, so ignore the overlay whenever the map is in front.
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay != null && NMapScreen.Instance is { IsOpen: true })
            overlay = null;

        if (overlay != null)
        {
            switch (overlay)
            {
                case NRewardsScreen rewards:
                    DetectRewards(ctx, rewards);
                    return;
                case NCardRewardSelectionScreen cardReward:
                    DetectCardReward(ctx, cardReward);
                    return;
                case NCardGridSelectionScreen grid:
                    DetectGridSelect(ctx, grid);
                    return;
                case NChooseACardSelectionScreen choose:
                    DetectCardChoice(ctx, choose);
                    return;
                case NChooseARelicSelection relic:
                    DetectRelicSelect(ctx, relic);
                    return;
                case NGameOverScreen gameOver:
                    DetectGameOver(ctx, gameOver);
                    return;
                case NChooseABundleSelectionScreen bundle:
                    DetectBundle(ctx, bundle);
                    return;
                case NCrystalSphereScreen sphere:
                    DetectCrystalSphere(ctx, sphere);
                    return;
                default:
                    if (HextechRuneSelectSupport.IsScreen(overlay))
                    {
                        DetectRuneSelect(ctx);
                        return;
                    }

                    ctx.Phase = "unsupported";
                    ctx.Actionable = false;
                    ctx.BlockedBy = "screen:" + overlay.GetType().Name;
                    ctx.Notes.Add($"The game is showing a screen this bridge does not drive ({overlay.GetType().Name}). Handle it manually.");
                    return;
            }
        }

        if (NMapScreen.Instance is { IsOpen: true } mapScreen)
        {
            DetectMap(ctx, mapScreen);
            return;
        }

        if (CombatManager.Instance.IsInProgress && ctx.Local?.PlayerCombatState != null)
        {
            DetectCombat(ctx);
            return;
        }

        var room = ctx.Run?.CurrentRoom;
        switch (room?.RoomType)
        {
            case RoomType.RestSite:
                DetectRest(ctx);
                return;
            case RoomType.Shop:
                DetectShop(ctx);
                return;
            case RoomType.Event:
                DetectEvent(ctx);
                return;
            case RoomType.Treasure:
                if (DetectTreasure(ctx))
                    return;
                break;
        }

        DetectProceedOnly(ctx);
    }

    private static void DetectCombat(Context ctx)
    {
        var player = ctx.Local!;
        var state = CombatManager.Instance.DebugOnlyGetState();
        var cm = CombatManager.Instance;

        ctx.Phase = "combat";
        var playerTurn = state?.CurrentSide == CombatSide.Player;
        var ending = cm.IsOverOrEnding;
        var readyToEnd = cm.IsPlayerReadyToEndTurn(player);
        ctx.Actionable = playerTurn && !ending && !readyToEnd && player.Creature.IsAlive;

        if (!playerTurn)
        {
            ctx.BlockedBy = "enemy_turn";
            ctx.Notes.Add("It is the enemy turn; call sts2_wait to block until your next turn starts.");
        }
        else if (readyToEnd)
        {
            ctx.BlockedBy = "turn_ending";
            ctx.Notes.Add("End-turn is already signalled; wait for the next turn.");
        }
        else if (ending)
        {
            ctx.BlockedBy = "combat_ending";
        }

        AppendCombatJson(ctx, player, state, withOptions: true);
    }

    /// <summary>
    /// Writes the <c>combat</c> section: HP / energy / stars / powers, the **live** hand, enemies and
    /// pile counts. <paramref name="withOptions"/> also publishes the hand as <c>options</c> indices,
    /// which only makes sense when the local human is driven through the UI actions; in AI mode dsh
    /// answers decisions instead and only needs the board itself.
    /// 战斗快照。AI 模式也带上实时手牌 —— 出牌/药水抽到的牌必须先让 dsh 看见。
    /// </summary>
    private static void AppendCombatJson(Context ctx, Player player, CombatState? state, bool withOptions)
    {
        var combat = player.PlayerCombatState;
        if (combat == null)
            return;

        var combatJson = new JsonObject
        {
            ["round"] = state?.RoundNumber ?? 0,
            ["side"] = state?.CurrentSide.ToString() ?? "None",
            ["player"] = new JsonObject
            {
                ["hp"] = player.Creature.CurrentHp,
                ["max_hp"] = player.Creature.MaxHp,
                ["block"] = player.Creature.Block,
                ["energy"] = combat.Energy,
                ["max_energy"] = combat.MaxEnergy,
                ["stars"] = combat.Stars,
                ["powers"] = PowersJson(player.Creature.Powers)
            },
            ["hand"] = new JsonArray(),
            ["enemies"] = new JsonArray(),
            ["piles"] = new JsonObject
            {
                ["draw"] = combat.DrawPile.Cards.Count,
                ["discard"] = combat.DiscardPile.Cards.Count,
                ["exhaust"] = combat.ExhaustPile.Cards.Count,
                ["play"] = combat.PlayPile.Cards.Count
            }
        };

        var handJson = (JsonArray)combatJson["hand"]!;
        var handIndex = 0;
        foreach (var card in combat.Hand.Cards)
        {
            var name = card.Title ?? card.Id.Entry;
            var playable = !card.Keywords.Contains(CardKeyword.Unplayable)
                           && combat.HasEnoughResourcesFor(card, out _);
            var desc = SafeCardDescription(card);
            handJson.Add(new JsonObject
            {
                ["index"] = handIndex,
                ["name"] = name,
                ["id"] = card.Id.Entry,
                ["cost"] = card.EnergyCost?.Canonical ?? 0,
                ["star_cost"] = Math.Max(0, card.GetStarCostWithModifiers()),
                ["type"] = card.Type.ToString(),
                ["upgraded"] = card.IsUpgraded,
                ["playable"] = playable,
                ["needs_target"] = card.TargetType is TargetType.AnyEnemy or TargetType.AnyPlayer
                    or TargetType.AnyAlly or TargetType.Osty,
                ["description"] = desc
            });
            if (withOptions)
            {
                ctx.Options.Add(new JsonObject
                {
                    ["index"] = handIndex,
                    ["kind"] = "card",
                    ["label"] = $"{name}{FormatCost(card)}",
                    ["detail"] = desc,
                    ["playable"] = playable
                });
            }
            handIndex++;
        }

        var enemiesJson = (JsonArray)combatJson["enemies"]!;
        var enemyIndex = 0;
        foreach (var enemy in state?.Enemies ?? Array.Empty<Creature>())
        {
            enemiesJson.Add(new JsonObject
            {
                ["index"] = enemyIndex,
                ["name"] = enemy.Name,
                ["id"] = enemy.Monster?.Id.Entry ?? "",
                ["hp"] = enemy.CurrentHp,
                ["max_hp"] = enemy.MaxHp,
                ["block"] = enemy.Block,
                ["alive"] = enemy.IsAlive,
                ["intent"] = SafeIntent(enemy)
            });
            enemyIndex++;
        }

        ctx.Json["combat"] = combatJson;
    }

    private static void DetectMap(Context ctx, NMapScreen screen)
    {
        ctx.Phase = "map";
        ctx.Actionable = true;
        var points = GetMapPoints(screen);
        if (points.Count == 0)
        {
            ctx.Actionable = false;
            ctx.BlockedBy = "map_empty";
            ctx.Notes.Add("The map is open but no travelable point is enabled yet. Call sts2_wait.");
            return;
        }

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i].Point;
            ctx.Options.Add(new JsonObject
            {
                ["index"] = i,
                ["kind"] = "map_node",
                ["label"] = ContextBuilder.DescribeMapPoint(point),
                ["row"] = point.coord.row,
                ["col"] = point.coord.col,
                ["type"] = point.PointType.ToString()
            });
        }

        ctx.Notes.Add("Choose a reachable node with sts2_choose index=<n>.");

        // Give dsh the same act-map view the built-in AI decides its route from (rows, node
        // types, child edges, visited marks), so a pick is not blind beyond the next row.
        AppendActMap(ctx);
        if (ctx.Json.ContainsKey("map"))
        {
            ctx.Notes.Add("`map` is the whole act: row by row, (col,row)type->children, √=visited. "
                          + "Plan the route several steps ahead like the built-in AI does.");
        }
    }

    /// <summary>
    /// Attaches <see cref="ContextBuilder.DescribeMap"/> under <c>map</c>. Only the map phase pays
    /// for it, and it is the exact text the built-in AI sees, so the two cannot drift apart.
    /// </summary>
    private static void AppendActMap(Context ctx)
    {
        if (ctx.Local == null)
            return;

        try
        {
            var text = ContextBuilder.DescribeMap(ctx.Local);
            if (!string.IsNullOrWhiteSpace(text))
                ctx.Json["map"] = text;
        }
        catch (Exception ex)
        {
            ctx.Notes.Add($"The act map could not be read ({ex.Message}); use sts2_read subject=map.");
        }
    }

    private static void DetectRewards(Context ctx, NRewardsScreen screen)
    {
        ctx.Phase = "rewards";
        var buttons = GetRewardButtons(screen);
        var anyEnabled = false;
        for (var i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            var reward = button.Reward;
            var enabled = button.IsEnabled;
            anyEnabled |= enabled;
            ctx.Options.Add(new JsonObject
            {
                ["index"] = i,
                ["kind"] = reward?.GetType().Name ?? "reward",
                ["label"] = reward == null ? "reward" : SafeLoc(reward.Description, "reward"),
                ["enabled"] = enabled
            });
        }

        var proceed = FindProceed(screen);
        ctx.Actionable = anyEnabled || proceed != null;
        if (!ctx.Actionable)
        {
            ctx.BlockedBy = "rewards_waiting";
            ctx.Notes.Add("The reward buttons are not ready yet; call sts2_wait.");
            return;
        }

        ctx.Notes.Add("Claim rewards with sts2_choose index=<n>; leave with sts2_proceed when done.");
    }

    private static void DetectCardReward(Context ctx, NCardRewardSelectionScreen screen)
    {
        ctx.Phase = "card_reward";
        var holders = GetCardRewardHolders(screen);
        var alternatives = GetCardRewardAlternatives(screen);

        for (var i = 0; i < holders.Count; i++)
        {
            var card = holders[i].CardModel;
            if (card == null)
                continue;
            ctx.Options.Add(new JsonObject
            {
                ["index"] = i,
                ["kind"] = "card_reward",
                ["label"] = $"{card.Title ?? card.Id.Entry}{FormatCost(card)}",
                ["detail"] = SafeCardDescription(card)
            });
        }

        for (var i = 0; i < alternatives.Count; i++)
        {
            ctx.Options.Add(new JsonObject
            {
                ["index"] = holders.Count + i,
                ["kind"] = "alternative",
                ["label"] = SafeNodeLabel(alternatives[i], "skip")
            });
        }

        ctx.Actionable = holders.Count > 0 || alternatives.Count > 0;
        if (!ctx.Actionable)
        {
            ctx.BlockedBy = "card_reward_empty";
            ctx.Notes.Add("The card reward screen has no selectable card yet; call sts2_wait.");
            return;
        }

        ctx.Notes.Add("Pick a card with sts2_choose index=<n>, or pick an alternative (skip) index.");
    }

    private static void DetectGridSelect(Context ctx, NCardGridSelectionScreen screen)
    {
        ctx.Phase = "card_select";
        var holders = GetGridHolders(screen);
        var (min, max) = GetGridSelectRange(screen);

        for (var i = 0; i < holders.Count; i++)
        {
            var card = holders[i].CardModel;
            if (card == null)
                continue;
            ctx.Options.Add(new JsonObject
            {
                ["index"] = i,
                ["kind"] = "card_select",
                ["label"] = $"{card.Title ?? card.Id.Entry}{FormatCost(card)}",
                ["detail"] = SafeCardDescription(card)
            });
        }

        ctx.Actionable = holders.Count > 0;
        if (!ctx.Actionable)
        {
            ctx.BlockedBy = "card_select_empty";
            ctx.Notes.Add("The card selection screen is not populated yet; call sts2_wait.");
            return;
        }

        ctx.Notes.Add($"Select {min}..{max} card(s) with sts2_select_cards indices=[...].");
    }

    private static void DetectCardChoice(Context ctx, NChooseACardSelectionScreen screen)
    {
        ctx.Phase = "card_choice";
        var holders = GetCardChoiceHolders(screen);

        for (var i = 0; i < holders.Count; i++)
        {
            var card = holders[i].CardModel;
            if (card == null)
                continue;
            ctx.Options.Add(new JsonObject
            {
                ["index"] = i,
                ["kind"] = "card_choice",
                ["label"] = $"{card.Title ?? card.Id.Entry}{FormatCost(card)}",
                ["detail"] = SafeCardDescription(card)
            });
        }

        var skip = FindSkipButton(screen);
        ctx.Actionable = holders.Count > 0 || skip != null;
        if (!ctx.Actionable)
        {
            ctx.BlockedBy = "card_choice_empty";
            ctx.Notes.Add("The card choice screen is not ready yet; call sts2_wait.");
            return;
        }

        ctx.Notes.Add("Choose with sts2_choose index=<n>, or skip with sts2_proceed / sts2_choose skip=true.");
    }

    private static void DetectRelicSelect(Context ctx, NChooseARelicSelection screen)
    {
        ctx.Phase = "relic_select";
        var holders = GetRelicHolders(screen);

        for (var i = 0; i < holders.Count; i++)
        {
            var model = holders[i].Relic?.Model;
            ctx.Options.Add(new JsonObject
            {
                ["index"] = i,
                ["kind"] = "relic",
                ["label"] = SafeLoc(model?.Title, model?.Id.Entry ?? "relic"),
                ["detail"] = SafeLoc(model?.DynamicDescription, "")
            });
        }

        var skip = FindSkipButton(screen);
        ctx.Actionable = holders.Count > 0 || skip != null;
        if (!ctx.Actionable)
        {
            ctx.BlockedBy = "relic_select_empty";
            ctx.Notes.Add("The relic selection screen is not ready yet; call sts2_wait.");
            return;
        }

        ctx.Notes.Add("Take a relic with sts2_choose index=<n>, or skip it.");
    }

    /// <summary>
    /// HextechRunes (ARAM: Mayhem) act rune pick. The screen belongs to a Steam Workshop mod (the picker is
    /// also used for forge choices and for the "no rune left" confirmation), so candidates come from its
    /// presenter surface and choosing goes through <c>PresenterChoose</c>.
    /// </summary>
    private static void DetectRuneSelect(Context ctx)
    {
        ctx.Phase = "rune_select";

        var screen = NOverlayStack.Instance?.Peek();
        if (screen == null || !HextechRuneSelectSupport.IsScreen(screen))
        {
            ctx.Actionable = false;
            ctx.BlockedBy = "rune_select_closed";
            ctx.Notes.Add("The rune selection screen is gone; call sts2_state again.");
            return;
        }

        if (HextechRuneSelectSupport.IsLocked(screen))
        {
            ctx.Actionable = false;
            ctx.BlockedBy = "rune_select_locked";
            ctx.Notes.Add("The rune is already locked in; call sts2_wait for the act to continue.");
            return;
        }

        var relics = HextechRuneSelectSupport.Options(screen);
        for (var i = 0; i < relics.Count; i++)
        {
            var relic = relics[i];
            var option = new JsonObject
            {
                ["index"] = i,
                ["kind"] = "rune",
                ["label"] = SafeLoc(relic.Title, relic.Id.Entry),
                ["detail"] = SafeLoc(relic.DynamicDescription, "")
            };

            var rarity = HextechRuneSelectSupport.RarityText(screen, i);
            if (!string.IsNullOrEmpty(rarity))
                option["rarity"] = rarity;

            var footer = HextechRuneSelectSupport.FooterText(screen, i);
            if (!string.IsNullOrWhiteSpace(footer))
                option["footer"] = footer;

            ctx.Options.Add(option);
        }

        if (relics.Count > 0)
        {
            ctx.Actionable = true;
            ctx.Notes.Add("Pick one rune with sts2_choose index=<n>; Hextech commits it immediately (no extra confirm).");
            return;
        }

        if (HextechRuneSelectSupport.CanConfirmEmpty(screen))
        {
            ctx.Options.Add(new JsonObject
            {
                ["index"] = 0,
                ["kind"] = "proceed",
                ["label"] = "确认（本幕不再有可选海克斯）"
            });
            ctx.Actionable = true;
            ctx.Notes.Add("This screen has no rune to pick; confirm it with sts2_choose index=0 or sts2_proceed.");
            return;
        }

        ctx.Actionable = false;
        ctx.BlockedBy = "rune_select_enemy_hex";
        ctx.Notes.Add("This Hextech screen only offers enemy-hex tweaks, which the bridge does not drive. Handle it manually.");
    }

    /// <summary>
    /// A target picker opened by some action (rest-site MEND, some potion/relic flows) is a global
    /// UI mode: it disables the controls underneath and waits for one node click. The options list
    /// is the selectable nodes (shared with <see cref="AgentActions"/>), plus a trailing cancel.
    /// 选目标阶段：休息处 MEND、部分药水/遗物会进入这里。
    /// </summary>
    private static bool DetectTargetSelect(Context ctx)
    {
        if (GetTargetManager()?.IsInSelection != true)
            return false;

        ctx.Phase = "target_select";
        var candidates = GetTargetCandidates();
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            ctx.Options.Add(new JsonObject
            {
                ["index"] = i,
                ["kind"] = candidate.Kind,
                ["label"] = candidate.Label,
                ["detail"] = $"HP {candidate.Hp}/{candidate.MaxHp}",
                ["hp"] = candidate.Hp,
                ["max_hp"] = candidate.MaxHp
            });
        }

        ctx.Options.Add(new JsonObject
        {
            ["index"] = candidates.Count,
            ["kind"] = "cancel",
            ["label"] = "Cancel targeting"
        });

        ctx.Actionable = true;
        ctx.Notes.Add(candidates.Count == 0
            ? "The game is asking for a target but none is selectable. Cancel with sts2_choose index=0."
            : $"The game is asking for a target. Pick one with sts2_choose index=<n> (0..{candidates.Count - 1}), "
              + $"or cancel with sts2_choose index={candidates.Count}.");
        return true;
    }

    private static void DetectRest(Context ctx)
    {
        ctx.Phase = "rest";
        var player = ctx.Local!;
        var room = NRestSiteRoom.Instance;
        var options = GetRestOptions(player);
        if (options.Count == 0)
        {
            // Once a rest-site option has been taken the options list empties out and the room
            // only offers the proceed button. Fall back to it instead of deadlocking on
            // "rest_not_ready"; a fresh room whose options are still loading has no enabled
            // proceed button, so this stays non-actionable until the room is really ready.
            DetectProceedOnly(ctx);
            if (ctx.Actionable)
                return;

            ctx.Phase = "rest";
            ctx.Actionable = false;
            ctx.BlockedBy = "rest_not_ready";
            ctx.Notes.Add("The rest site options are not available yet; call sts2_wait.");
            return;
        }

        var anyEnabled = false;
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var enabled = option.IsEnabled && IsRestButtonEnabled(room, option);
            anyEnabled |= enabled;
            ctx.Options.Add(new JsonObject
            {
                ["index"] = i,
                ["kind"] = option.OptionId,
                ["label"] = SafeLoc(option.Title, option.OptionId),
                ["detail"] = SafeLoc(option.Description, ""),
                ["enabled"] = enabled
            });
        }

        ctx.Actionable = anyEnabled;
        if (!anyEnabled)
        {
            // A choice was already made (buttons are spent); the room only has proceed left.
            DetectProceedOnly(ctx);
            if (ctx.Actionable)
                return;

            ctx.Phase = "rest";
            ctx.Actionable = false;
            ctx.BlockedBy = "rest_waiting";
            ctx.Notes.Add("No rest-site option is selectable right now (maybe a choice was already made). Proceed to leave.");
            return;
        }

        ctx.Notes.Add("Pick an option with sts2_choose index=<n>. SMITH opens a card selection screen afterwards.");
    }

    private static void DetectShop(Context ctx)
    {
        ctx.Phase = "shop";
        var room = NMerchantRoom.Instance;
        if (room == null || !GodotObject.IsInstanceValid(room))
        {
            ctx.Actionable = false;
            ctx.BlockedBy = "shop_not_ready";
            ctx.Notes.Add("The merchant room is not ready yet; call sts2_wait.");
            return;
        }

        var entries = GetShopEntries(room);
        var player = ctx.Local!;
        ctx.Actionable = true;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            ctx.Options.Add(new JsonObject
            {
                ["index"] = i,
                ["kind"] = entry.GetType().Name,
                ["label"] = ShopLabel(entry),
                ["cost"] = entry.Cost,
                ["affordable"] = entry.Cost <= player.Gold,
                ["stocked"] = entry.IsStocked
            });
        }

        ctx.Notes.Add("Buy with sts2_choose index=<n>; sts2_proceed closes the shelf, then leaves the shop.");
    }

    private static void DetectEvent(Context ctx)
    {
        ctx.Phase = "event";
        var player = ctx.Local!;
        var eventSync = RunManager.Instance?.EventSynchronizer;
        if (eventSync == null)
        {
            ctx.Actionable = false;
            ctx.BlockedBy = "event_not_ready";
            return;
        }

        EventModel evt;
        try { evt = eventSync.GetEventForPlayer(player); }
        catch (Exception ex)
        {
            ctx.Actionable = false;
            ctx.BlockedBy = "event_not_ready";
            ctx.Notes.Add("Could not read the event: " + ex.Message);
            return;
        }

        // Options are enumerated from the clickable buttons, not from the model: that is what a human
        // clicks, and some layouts (ancient dialogue, custom events) only exist in the UI.
        var buttons = GetEventOptionButtons();
        if (buttons.Count > 0)
        {
            for (var i = 0; i < buttons.Count; i++)
            {
                var option = buttons[i].Option;
                ctx.Options.Add(new JsonObject
                {
                    ["index"] = i,
                    ["kind"] = option.IsProceed ? "proceed" : "event_option",
                    ["label"] = SafeLoc(option.Title, "option"),
                    ["detail"] = SafeLoc(option.Description, ""),
                    ["locked"] = option.IsLocked,
                    ["proceed"] = option.IsProceed
                });
            }

            ctx.Actionable = true;
            ctx.Notes.Add("Choose an option with sts2_choose index=<n>.");
            return;
        }

        var dialogue = FindEventDialogueButton();
        if (dialogue != null)
        {
            ctx.Options.Add(new JsonObject
            {
                ["index"] = 0,
                ["kind"] = "dialogue",
                ["label"] = "Continue the event dialogue"
            });
            ctx.Actionable = true;
            ctx.Notes.Add("The event is playing its intro; advance it with sts2_choose index=0 or sts2_proceed.");
            return;
        }

        // Custom event layouts (for example the fake merchant) only offer the standard proceed button.
        if (FindEnabledProceed() != null)
        {
            ctx.Options.Add(new JsonObject
            {
                ["index"] = 0,
                ["kind"] = "proceed",
                ["label"] = "Leave the event"
            });
            ctx.Actionable = true;
            ctx.Notes.Add("Use sts2_proceed to leave the event.");
            return;
        }

        if (evt.IsFinished)
        {
            ctx.Actionable = false;
            ctx.BlockedBy = "event_finished";
            ctx.Notes.Add("The event is finished; use sts2_proceed to leave the room.");
            return;
        }

        ctx.Actionable = false;
        ctx.BlockedBy = "event_waiting";
        ctx.Notes.Add("The event has no selectable option right now; call sts2_wait.");
    }

    private static void DetectBundle(Context ctx, NChooseABundleSelectionScreen screen)
    {
        ctx.Phase = "bundle_select";

        var preview = screen.GetNodeOrNull<Control>("%BundlePreviewContainer");
        if (preview is { Visible: true })
        {
            var confirm = screen.GetNodeOrNull<NConfirmButton>("%Confirm");
            var cancel = screen.GetNodeOrNull<NBackButton>("%Cancel");

            if (confirm is { IsEnabled: true } && GodotObject.IsInstanceValid(confirm))
            {
                ctx.Options.Add(new JsonObject
                {
                    ["index"] = 0,
                    ["kind"] = "bundle_confirm",
                    ["label"] = "Confirm this bundle"
                });
            }

            if (cancel is { IsEnabled: true } && GodotObject.IsInstanceValid(cancel))
            {
                ctx.Options.Add(new JsonObject
                {
                    ["index"] = ctx.Options.Count,
                    ["kind"] = "bundle_cancel",
                    ["label"] = "Back to the bundle list"
                });
            }

            ctx.Actionable = ctx.Options.Count > 0;
            if (!ctx.Actionable)
            {
                ctx.BlockedBy = "bundle_waiting";
                ctx.Notes.Add("The bundle preview is still opening; call sts2_wait.");
                return;
            }

            ctx.Notes.Add("Take this bundle with sts2_choose index=0, or go back with the other index.");
            return;
        }

        var bundles = UiHelper.FindAll<NCardBundle>(screen)
            .Where(b => GodotObject.IsInstanceValid(b) && b.IsVisibleInTree() && b.Bundle != null)
            .ToList();

        for (var i = 0; i < bundles.Count; i++)
        {
            var names = string.Join(" + ", bundles[i].Bundle.Select(CardLabel));
            ctx.Options.Add(new JsonObject
            {
                ["index"] = i,
                ["kind"] = "bundle",
                ["label"] = names,
                ["cards"] = new JsonArray(bundles[i].Bundle
                    .Select(c => (JsonNode)new JsonObject
                    {
                        ["name"] = c.Title ?? c.Id.Entry,
                        ["id"] = c.Id.Entry
                    })
                    .ToArray())
            });
        }

        ctx.Actionable = bundles.Count > 0;
        if (!ctx.Actionable)
        {
            ctx.BlockedBy = "bundle_empty";
            ctx.Notes.Add("The bundle screen is not populated yet; call sts2_wait.");
            return;
        }

        ctx.Notes.Add("Pick a bundle with sts2_choose index=<n>; the next snapshot asks you to confirm it.");
    }

    private static void DetectCrystalSphere(Context ctx, NCrystalSphereScreen screen)
    {
        ctx.Phase = "crystal_sphere";

        var entity = AccessTools.Field(typeof(NCrystalSphereScreen), "_entity")?.GetValue(screen)
            as CrystalSphereMinigame;
        var divinations = entity?.DivinationCount ?? 0;
        var finished = entity?.IsFinished ?? false;

        var proceed = screen.GetNodeOrNull<NProceedButton>("%ProceedButton");
        var proceedEnabled = proceed != null
                             && GodotObject.IsInstanceValid(proceed)
                             && proceed.IsEnabled
                             && proceed.IsVisibleInTree();

        var cells = GetCrystalCells(screen);
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            ctx.Options.Add(new JsonObject
            {
                ["index"] = i,
                ["kind"] = "crystal_cell",
                ["x"] = cell.Entity.X,
                ["y"] = cell.Entity.Y,
                ["label"] = $"hidden cell ({cell.Entity.X},{cell.Entity.Y})"
            });
        }

        if (!finished && divinations > 0)
        {
            ctx.Options.Add(new JsonObject
            {
                ["index"] = cells.Count,
                ["kind"] = "crystal_tool",
                ["tool"] = "small",
                ["label"] = "Small divination (plus-shaped reveal)"
            });
            ctx.Options.Add(new JsonObject
            {
                ["index"] = cells.Count + 1,
                ["kind"] = "crystal_tool",
                ["tool"] = "big",
                ["label"] = "Big divination (wide reveal)"
            });
        }

        ctx.Json["crystal_sphere"] = new JsonObject
        {
            ["divinations"] = divinations,
            ["finished"] = finished,
            ["hidden_cells"] = cells.Count,
            ["proceed_available"] = proceedEnabled
        };

        ctx.Actionable = (!finished && cells.Count > 0) || proceedEnabled;
        if (!ctx.Actionable)
        {
            ctx.BlockedBy = "crystal_sphere_waiting";
            ctx.Notes.Add("The minigame is animating; call sts2_wait.");
            return;
        }

        ctx.Notes.Add(finished
            ? "The minigame is over; use sts2_proceed."
            : "Click a hidden cell with sts2_choose index=<n>; the last two indices switch divination tools. "
              + "Use sts2_proceed once the divinations run out.");
    }

    private static bool DetectTreasure(Context ctx)
    {
        ctx.Phase = "treasure";
        var runNode = NRun.Instance;
        var chest = FindTreasureChest(runNode);
        if (chest != null)
        {
            ctx.Actionable = true;
            ctx.Notes.Add("Open the chest with sts2_proceed, then pick a relic.");
            return true;
        }

        // The chest is open: the relic it holds is an in-room card (NTreasureRoomRelicCollection),
        // not an overlay, so it never shows up in the overlay-stack switch above.
        var holders = GetTreasureRelicHolders(runNode);
        if (holders.Count > 0)
        {
            ctx.Phase = "treasure_relic";
            for (var i = 0; i < holders.Count; i++)
            {
                var model = holders[i].Relic?.Model;
                ctx.Options.Add(new JsonObject
                {
                    ["index"] = i,
                    ["kind"] = "treasure_relic",
                    ["label"] = SafeLoc(model?.Title, model?.Id.Entry ?? "relic"),
                    ["detail"] = SafeLoc(model?.DynamicDescription, "")
                });
            }

            ctx.Actionable = true;
            ctx.Notes.Add("Take the treasure relic with sts2_choose index=<n>.");
            return true;
        }

        // The chest was opened already and the relic overlay has not appeared yet.
        if (NOverlayStack.Instance?.Peek() != null)
            return false;

        // Chest finished and nothing left to take: the room only offers its proceed button now.
        DetectProceedOnly(ctx);
        if (ctx.Actionable)
            return true;

        ctx.Phase = "treasure";
        ctx.Actionable = false;
        ctx.BlockedBy = "treasure_waiting";
        ctx.Notes.Add("The treasure room is not ready yet; call sts2_wait.");
        return true;
    }

    private static void DetectGameOver(Context ctx, NGameOverScreen screen)
    {
        ctx.Phase = "game_over";
        var continueButton = UiHelper.FindFirst<NGameOverContinueButton>(screen);
        var menuButton = UiHelper.FindFirst<NReturnToMainMenuButton>(screen);
        ctx.Actionable = (continueButton is { Visible: true, IsEnabled: true })
                         || (menuButton is { Visible: true, IsEnabled: true });
        if (!ctx.Actionable)
        {
            ctx.BlockedBy = "game_over_waiting";
            ctx.Notes.Add("Wait for the game-over summary to finish animating.");
            return;
        }

        ctx.Notes.Add("Use sts2_proceed to advance the game-over screen.");
    }

    private static void DetectProceedOnly(Context ctx)
    {
        ctx.Phase = "room_proceed";
        var proceed = FindEnabledProceed();
        if (proceed == null)
        {
            ctx.Phase = "idle";
            ctx.Actionable = false;
            ctx.BlockedBy = "idle";
            ctx.Notes.Add("Nothing to do right now. Call sts2_wait for the next event.");
            return;
        }

        ctx.Actionable = true;
        ctx.Notes.Add("Use sts2_proceed to continue.");
    }

    private static void AppendNextStepHint(Context ctx)
    {
        switch (ctx.Phase)
        {
            case "combat":
                ctx.Notes.Add("Play cards with sts2_play_card, potions with sts2_use_potion; finish the turn with sts2_end_turn.");
                break;
            case "map":
                ctx.Notes.Add("Travel with sts2_choose index=<n>.");
                break;
            case "rewards":
                ctx.Notes.Add("Use sts2_choose index=<n> to claim, sts2_proceed to leave.");
                break;
            case "card_select":
                ctx.Notes.Add("Use sts2_select_cards indices=[...].");
                break;
            case "card_reward":
            case "card_choice":
                ctx.Notes.Add("Use sts2_choose index=<n>.");
                break;
            case "bundle_select":
                ctx.Notes.Add("Use sts2_choose index=<n>; confirm the bundle pick when asked.");
                break;
            case "crystal_sphere":
                ctx.Notes.Add("Click hidden cells with sts2_choose index=<n> (last two indices switch tools), then sts2_proceed.");
                break;
            case "relic_select":
                ctx.Notes.Add("Use sts2_choose index=<n> or sts2_proceed to skip.");
                break;
            case "rest":
                ctx.Notes.Add("Use sts2_choose index=<n>.");
                break;
            case "shop":
                ctx.Notes.Add("Use sts2_choose index=<n> to buy, sts2_proceed to leave.");
                break;
            case "target_select":
                ctx.Notes.Add("Use sts2_choose index=<n> to pick a target (the last index cancels).");
                break;
            case "event":
                ctx.Notes.Add("Use sts2_choose index=<n>.");
                break;
            case "treasure":
                ctx.Notes.Add("Use sts2_proceed to open the chest.");
                break;
            case "treasure_relic":
                ctx.Notes.Add("Use sts2_choose index=<n> to take the treasure relic.");
                break;
            case "room_proceed":
                ctx.Notes.Add("Use sts2_proceed.");
                break;
            case "game_over":
                ctx.Notes.Add("Use sts2_proceed.");
                break;
        }
    }

    // ---- Shared enumeration (also used by AgentActions) -----------------------------------

    internal static List<NMapPoint> GetMapPoints(NMapScreen screen) =>
        UiHelper.FindAll<NMapPoint>(screen)
            .Where(p => GodotObject.IsInstanceValid(p)
                        && p.IsEnabled
                        && p.Point != null
                        && p.State == MapPointState.Travelable)
            .OrderBy(p => p.Point.coord.row)
            .ThenBy(p => p.Point.coord.col)
            .ToList();

    internal static List<NRewardButton> GetRewardButtons(NRewardsScreen screen) =>
        UiHelper.FindAll<NRewardButton>(screen)
            .Where(b => GodotObject.IsInstanceValid(b) && b.Reward != null)
            .ToList();

    internal static List<NCardHolder> GetCardRewardHolders(NCardRewardSelectionScreen screen) =>
        UiHelper.FindAll<NCardHolder>(screen)
            .Where(h => GodotObject.IsInstanceValid(h) && h.Visible && h.CardModel != null)
            .ToList();

    internal static List<NCardRewardAlternativeButton> GetCardRewardAlternatives(NCardRewardSelectionScreen screen) =>
        UiHelper.FindAll<NCardRewardAlternativeButton>(screen)
            .Where(b => GodotObject.IsInstanceValid(b) && b.Visible)
            .ToList();

    internal static List<NCardHolder> GetGridHolders(NCardGridSelectionScreen screen) =>
        UiHelper.FindAll<NCardHolder>(screen)
            .Where(h => GodotObject.IsInstanceValid(h) && h.Visible && h.CardModel != null)
            .ToList();

    internal static List<NCardHolder> GetCardChoiceHolders(NChooseACardSelectionScreen screen) =>
        UiHelper.FindAll<NCardHolder>(screen)
            .Where(h => GodotObject.IsInstanceValid(h) && h.Visible && h.CardModel != null)
            .ToList();

    internal static List<NRelicBasicHolder> GetRelicHolders(NChooseARelicSelection screen) =>
        UiHelper.FindAll<NRelicBasicHolder>(screen)
            .Where(h => GodotObject.IsInstanceValid(h) && h.Visible && h.Relic?.Model != null)
            .ToList();

    /// <summary>Visible, unlocked event option buttons in the current run, in UI order.</summary>
    internal static List<NEventOptionButton> GetEventOptionButtons()
    {
        var runNode = NRun.Instance;
        if (runNode == null || !GodotObject.IsInstanceValid(runNode))
            return new List<NEventOptionButton>();

        return UiHelper.FindAll<NEventOptionButton>(runNode)
            .Where(b => GodotObject.IsInstanceValid(b)
                        && b.IsVisibleInTree()
                        && b.IsEnabled
                        && b.Option != null
                        && !b.Option.IsLocked)
            .ToList();
    }

    /// <summary>
    /// The ancient-event dialogue hitbox, which has to be clicked through before the options appear
    /// (this is also the very first thing a fresh run shows). 古代事件对话。
    /// </summary>
    internal static NButton? FindEventDialogueButton()
    {
        var runNode = NRun.Instance;
        if (runNode == null || !GodotObject.IsInstanceValid(runNode))
            return null;

        foreach (var layout in UiHelper.FindAll<NAncientEventLayout>(runNode))
        {
            if (!GodotObject.IsInstanceValid(layout) || !layout.IsVisibleInTree())
                continue;

            var hitbox = layout.GetNodeOrNull<NButton>("%DialogueHitbox");
            if (hitbox != null && GodotObject.IsInstanceValid(hitbox)
                               && hitbox.IsVisibleInTree() && hitbox.IsEnabled)
                return hitbox;
        }

        return null;
    }

    /// <summary>Still-hidden crystal-sphere cells, ordered by (x, y) so indices are stable.</summary>
    internal static List<NCrystalSphereCell> GetCrystalCells(NCrystalSphereScreen screen) =>
        UiHelper.FindAll<NCrystalSphereCell>(screen)
            .Where(c => GodotObject.IsInstanceValid(c)
                        && c.Visible
                        && c.Entity != null
                        && c.Entity.IsHidden)
            .OrderBy(c => c.Entity.X)
            .ThenBy(c => c.Entity.Y)
            .ToList();

    /// <summary>Same label the rest of the snapshot uses for a card ("name(cost)").</summary>
    internal static string CardLabel(CardModel card) =>
        $"{card.Title ?? card.Id.Entry}{FormatCost(card)}";

    internal static (int Min, int Max) GetGridSelectRange(NCardGridSelectionScreen screen)
    {
        var min = 1;
        var max = 1;
        try
        {
            var prefs = AccessTools.Field(typeof(NCardGridSelectionScreen), "_prefs")?.GetValue(screen);
            if (prefs != null)
            {
                min = (int)(AccessTools.Property(prefs.GetType(), "MinSelect")?.GetValue(prefs) ?? 1);
                max = (int)(AccessTools.Property(prefs.GetType(), "MaxSelect")?.GetValue(prefs) ?? min);
            }
        }
        catch { /* defaults */ }

        if (max < min)
            max = min;
        return (min, max);
    }

    internal static List<RestSiteOption> GetRestOptions(Player player)
    {
        try
        {
            var sync = RunManager.Instance?.RestSiteSynchronizer;
            return sync?.GetOptionsForPlayer(player)?.ToList() ?? new List<RestSiteOption>();
        }
        catch
        {
            return new List<RestSiteOption>();
        }
    }

    /// <summary>One node the game's active target picker accepts. 选目标候选。</summary>
    internal sealed class TargetCandidate
    {
        public Node? Node { get; init; }
        public string Label { get; init; } = "";
        public string Kind { get; init; } = "";
        public int Hp { get; init; }
        public int MaxHp { get; init; }
    }

    /// <summary>The global target picker. 全局目标选择器。</summary>
    internal static NTargetManager? GetTargetManager()
    {
        try
        {
            var runNode = NRun.Instance;
            if (runNode == null || !GodotObject.IsInstanceValid(runNode))
                return null;
            return runNode.GlobalUi?.TargetManager;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Nodes the active target picker accepts, in UI order. The snapshot publishes these indices and
    /// <see cref="AgentActions"/> resolves the same list, so what dsh saw is what it clicks.
    /// 目标候选：遵守 NTargetManager 自己的过滤（例如 MEND 不能选自己）。
    /// </summary>
    internal static List<TargetCandidate> GetTargetCandidates()
    {
        var candidates = new List<TargetCandidate>();
        var manager = GetTargetManager();
        if (manager?.IsInSelection != true)
            return candidates;

        var seenPlayers = new HashSet<Player>();

        bool Allowed(Node node)
        {
            try
            {
                return GodotObject.IsInstanceValid(node) && manager.AllowedToTargetNode(node);
            }
            catch
            {
                return false;
            }
        }

        void Add(Node node, Player? player, string kind, int hp, int maxHp)
        {
            if (player != null && (!seenPlayers.Add(player) || player.Creature == null || !player.Creature.IsAlive))
                return;

            var label = player?.Creature?.Name
                        ?? (node is NCreature creatureNode ? creatureNode.Entity?.Name : null)
                        ?? "target";
            candidates.Add(new TargetCandidate
            {
                Node = node,
                Label = label,
                Kind = player != null ? "player" : kind,
                Hp = hp,
                MaxHp = maxHp
            });
        }

        // Combat: creature nodes cover enemies and (for ally effects) player creatures.
        var combatRoom = NCombatRoom.Instance;
        if (combatRoom != null && GodotObject.IsInstanceValid(combatRoom))
        {
            foreach (var node in combatRoom.CreatureNodes)
            {
                if (!Allowed(node))
                    continue;
                var entity = node.Entity;
                if (entity == null)
                    continue;
                Add(node, entity.Player, entity.IsEnemy ? "enemy" : "creature", entity.CurrentHp, entity.MaxHp);
            }
        }

        // Rest site: the character sprites in the room (MEND targets these).
        var restRoom = NRestSiteRoom.Instance;
        if (restRoom != null && GodotObject.IsInstanceValid(restRoom))
        {
            foreach (var character in restRoom.characterAnims)
            {
                if (!Allowed(character))
                    continue;
                var player = character.Player;
                Add(character, player, "rest_site",
                    player?.Creature?.CurrentHp ?? 0, player?.Creature?.MaxHp ?? 0);
            }
        }

        // Multiplayer: the top-left player state panels are valid targets too.
        var runNode = NRun.Instance;
        if (runNode != null && GodotObject.IsInstanceValid(runNode))
        {
            foreach (var state in UiHelper.FindAll<NMultiplayerPlayerState>(runNode))
            {
                if (!Allowed(state))
                    continue;
                var player = state.Player;
                Add(state, player, "player_state",
                    player?.Creature?.CurrentHp ?? 0, player?.Creature?.MaxHp ?? 0);
            }
        }

        return candidates;
    }

    internal static List<MerchantEntry> GetShopEntries(NMerchantRoom room)
    {
        try
        {
            var inventory = room.Inventory?.Inventory;
            return inventory?.AllEntries.Where(e => e.IsStocked).ToList() ?? new List<MerchantEntry>();
        }
        catch
        {
            return new List<MerchantEntry>();
        }
    }

    internal static NProceedButton? FindEnabledProceed()
    {
        var runNode = NRun.Instance;
        if (runNode == null || !GodotObject.IsInstanceValid(runNode))
            return null;
        return UiHelper.FindAll<NProceedButton>(runNode)
            .FirstOrDefault(b => GodotObject.IsInstanceValid(b) && b.IsVisibleInTree() && b.IsEnabled);
    }

    internal static NProceedButton? FindProceed(Node root) =>
        UiHelper.FindAll<NProceedButton>(root)
            .FirstOrDefault(b => GodotObject.IsInstanceValid(b) && b.IsVisibleInTree() && b.IsEnabled);

    internal static NClickableControl? FindTreasureChest(Node? runNode)
    {
        if (runNode == null || !GodotObject.IsInstanceValid(runNode))
            return null;
        var room = UiHelper.FindAll<NTreasureRoom>(runNode)
            .FirstOrDefault(r => GodotObject.IsInstanceValid(r) && r.IsVisibleInTree());
        if (room == null)
            return null;
        var chest = room.GetNodeOrNull<NClickableControl>("Chest");
        return chest != null && GodotObject.IsInstanceValid(chest) && chest.IsVisibleInTree() && chest.IsEnabled
            ? chest
            : null;
    }

    internal static NButton? FindSkipButton(Node root) =>
        UiHelper.FindAll<NChoiceSelectionSkipButton>(root)
            .FirstOrDefault(b => GodotObject.IsInstanceValid(b) && b.IsVisibleInTree() && b.IsEnabled);

    /// <summary>
    /// The relic card(s) shown inside an opened treasure chest. Singleplayer shows one holder
    /// (<c>%SingleplayerRelicHolder</c>); multiplayer with several relics shows one per relic.
    /// </summary>
    internal static List<NTreasureRoomRelicHolder> GetTreasureRelicHolders(Node? runNode)
    {
        if (runNode == null || !GodotObject.IsInstanceValid(runNode))
            return new List<NTreasureRoomRelicHolder>();
        return UiHelper.FindAll<NTreasureRoomRelicHolder>(runNode)
            .Where(h => GodotObject.IsInstanceValid(h)
                        && h.IsVisibleInTree()
                        && h.IsEnabled
                        && h.Relic?.Model != null)
            .ToList();
    }

    internal static bool IsRestButtonEnabled(NRestSiteRoom? room, RestSiteOption option)
    {
        if (room == null || !GodotObject.IsInstanceValid(room))
            return option.IsEnabled;
        var button = room.GetButtonForOption(option);
        return button == null || !GodotObject.IsInstanceValid(button) || button.IsEnabled;
    }

    // ---- Formatting helpers ---------------------------------------------------------------

    internal static string SafeLoc(MegaCrit.Sts2.Core.Localization.LocString? loc, string fallback) =>
        PromptTextHelper.ResolveLocString(loc, fallback);

    internal static string SafeCardDescription(CardModel card) =>
        PromptTextHelper.CleanText(PromptTextHelper.GetCardDescriptionText(card, PileType.Hand));

    internal static string FormatCost(CardModel card)
    {
        var cost = card.EnergyCost?.Canonical ?? 0;
        var star = Math.Max(0, card.GetStarCostWithModifiers());
        return star > 0 ? $"({cost}费/{star}星)" : $"({cost}费)";
    }

    internal static string SafeIntent(Creature enemy)
    {
        try
        {
            var move = enemy.Monster?.NextMove;
            return move == null ? "" : ContextBuilder.FormatIntentCompact(move);
        }
        catch
        {
            return "";
        }
    }

    internal static JsonArray PowersJson(IEnumerable<PowerModel>? powers)
    {
        var array = new JsonArray();
        if (powers == null)
            return array;
        foreach (var power in powers)
        {
            array.Add(new JsonObject
            {
                ["name"] = SafeLoc(power.Title, power.Id.Entry),
                ["id"] = power.Id.Entry,
                ["amount"] = power.Amount
            });
        }
        return array;
    }

    internal static string ShopLabel(MerchantEntry entry)
    {
        try
        {
            var text = entry switch
            {
                MerchantCardEntry cardEntry => "card: " + (cardEntry.CreationResult?.Card?.Title ?? "?"),
                MerchantRelicEntry relicEntry => "relic: " + SafeLoc(relicEntry.Model?.Title, relicEntry.Model?.Id.Entry ?? "?"),
                MerchantPotionEntry potionEntry => "potion: " + SafeLoc(potionEntry.Model?.Title, potionEntry.Model?.Id.Entry ?? "?"),
                MerchantCardRemovalEntry => "card removal",
                _ => entry.GetType().Name
            };
            return $"{text} [{entry.Cost} gold]";
        }
        catch
        {
            return entry.GetType().Name;
        }
    }

    internal static string SafeNodeLabel(Node node, string fallback)
    {
        try
        {
            var labelField = AccessTools.Field(node.GetType(), "_label")?.GetValue(node);
            if (labelField is RichTextLabel rich)
                return PromptTextHelper.CleanText(rich.Text);
            if (labelField is Label label)
                return PromptTextHelper.CleanText(label.Text);
            var child = UiHelper.FindFirst<RichTextLabel>(node);
            if (child != null)
                return PromptTextHelper.CleanText(child.Text);
        }
        catch { }
        return fallback;
    }

    /// <summary>Potion slots as {name,id} entries. 药水槽序列化（空槽不会出现在 Player.Potions 里）。</summary>
    private static JsonArray PotionsJson(IEnumerable<PotionModel> potions) =>
        Names(potions.Select(p => (SafeLoc(p.Title, p.Id.Entry), p.Id.Entry)));

    private static JsonArray Names(IEnumerable<(string Name, string Id)> values)
    {
        var array = new JsonArray();
        foreach (var (name, id) in values)
            array.Add(new JsonObject { ["name"] = name, ["id"] = id });
        return array;
    }
}
