using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.DailyRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Timeline.Epochs;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Localization;
using AiTeammate.AiTeammateCode.Solo;
using AiTeammate.AiTeammateCode.Ui;

namespace AiTeammate.AiTeammateCode.Agent;

/// <summary>
/// One pressable (or tickable) thing on the current menu screen. <see cref="Index"/> is the number
/// <c>sts2_choose</c> accepts, and <see cref="Node"/> is the live node re-pressed by
/// <see cref="AgentMenuSupport.TryPress"/>.
/// 菜单里一项可按下/可勾选的东西。
/// </summary>
internal sealed class AgentMenuOption
{
    public int Index;
    public string Kind = "button";
    public string Id = "";
    public string Label = "";
    public bool Enabled = true;
    public bool Ticked;
    public Node? Node;

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["index"] = Index,
            ["kind"] = Kind,
            ["id"] = Id,
            ["label"] = Label,
            ["enabled"] = Enabled
        };

        // Tickboxes are the only options with a second state worth reporting.
        if (Kind == "modifier")
            json["ticked"] = Ticked;

        return json;
    }
}

/// <summary>
/// The main menu as the external agent sees it: phase/screen classification, the pressable options
/// in tree order, and the screen-specific JSON (<c>menu</c> in the snapshot).
/// 主菜单快照：阶段分类 + 可选项 + 界面 JSON。
/// </summary>
internal sealed class AgentMenuCapture
{
    public string Phase = "menu";
    public string Screen = "main";
    public string Title = "";
    public int Depth;
    public bool Actionable;
    public string BlockedBy = "";
    public List<AgentMenuOption> Options = new();
    public JsonObject Menu = new();
    public List<string> Notes = new();
}

/// <summary>
/// Drives the vanilla main menu (and its submenus / lobbies) through the same nodes the player
/// clicks, so the external agent can see and press what a human would.
///
/// Everything here runs on the Godot main thread (the bridge marshals through
/// <see cref="MainThread"/>). Game APIs differ between 0.107.1 and 0.111.0 (lobby player renaming,
/// private lobby fields), so anything version-specific is isolated in reflection or
/// <see cref="GameCompat"/>. 主菜单/子菜单/大厅都由这里暴露给外部 Agent。
/// </summary>
internal static class AgentMenuSupport
{
    private const int MaxOptions = 80;
    private const int MaxDepth = 24;
    private const int MaxLabelLength = 160;

    // 0.107.1 and 0.111.0 both keep the daily lobby in a private field. NSubmenuStack keeps the
    // submenu stack private as well; both are read for depth classification only.
    private static readonly FieldInfo? DailyLobbyField = AccessTools.Field(typeof(NDailyRunScreen), "_lobby");
    private static readonly FieldInfo? SubmenuStackField = AccessTools.Field(typeof(NSubmenuStack), "_submenus");

    // ---- Public surface ---------------------------------------------------------------------

    /// <summary>True when the main menu is showing its root button list. 主菜单在根层。</summary>
    public static bool IsRootMenu
    {
        get
        {
            try
            {
                var mainMenu = NGame.Instance?.MainMenu;
                return mainMenu != null
                       && GodotObject.IsInstanceValid(mainMenu)
                       && PeekSubmenu(mainMenu) == null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>True when any main-menu submenu (lobby / settings / compendium ...) is open. 有子菜单打开。</summary>
    public static bool HasOpenSubmenu
    {
        get
        {
            try
            {
                var mainMenu = NGame.Instance?.MainMenu;
                return mainMenu != null
                       && GodotObject.IsInstanceValid(mainMenu)
                       && PeekSubmenu(mainMenu) != null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Reads the current main menu. Returns false when the menu is not on screen yet (loading), so
    /// the snapshot can report <c>phase=idle</c> instead of a bogus menu.
    /// </summary>
    public static bool TryCapture(out AgentMenuCapture capture)
    {
        capture = new AgentMenuCapture();

        NMainMenu? mainMenu = null;
        try
        {
            mainMenu = NGame.Instance?.MainMenu;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Agent] main menu lookup failed: {ex.Message}");
            return false;
        }

        if (mainMenu == null || !GodotObject.IsInstanceValid(mainMenu))
            return false;

        try
        {
            var top = PeekSubmenu(mainMenu);
            if (top == null)
                FillRoot(capture, mainMenu);
            else
                FillSubmenu(capture, mainMenu, top);
        }
        catch (Exception ex)
        {
            // A missing node must never break the snapshot: degrade to an inert "other" screen.
            MainFile.Logger.Warn($"[Agent] menu capture failed: {ex.Message}");
            capture = new AgentMenuCapture
            {
                Phase = "menu",
                Screen = "other",
                Title = "Main menu",
                Actionable = false,
                BlockedBy = "capture_error"
            };
            capture.Menu["screen"] = capture.Screen;
            capture.Menu["title"] = capture.Title;
            capture.Notes.Add("The menu could not be read: " + ex.Message);
            return true;
        }

        var modal = SafeModalName();
        if (modal != null)
        {
            capture.Actionable = false;
            capture.BlockedBy = "modal";
            capture.Notes.Add($"A modal dialog is open ({modal}); close it before using the menu.");
        }

        return true;
    }

    /// <summary>
    /// Rebuilds the capture and presses option <paramref name="index"/>. The index is re-resolved
    /// against the live tree, so a stale index fails cleanly instead of pressing the wrong node.
    /// </summary>
    public static bool TryPress(int index, out string detail)
    {
        detail = "";
        try
        {
            if (!TryCapture(out var capture))
            {
                detail = "The main menu is not open.";
                return false;
            }

            if (index < 0 || index >= capture.Options.Count)
            {
                detail = $"Option index out of range (0..{capture.Options.Count - 1}).";
                return false;
            }

            var option = capture.Options[index];
            if (!option.Enabled)
            {
                detail = $"Menu option {index} [{option.Label}] is disabled.";
                return false;
            }

            switch (option.Kind)
            {
                case "character":
                    return PressCharacter(option, out detail);
                case "ai_character":
                    return PressAiCharacter(option, out detail);
                case "modifier":
                    return PressModifier(option, out detail);
                case "ai_join":
                    return PressAiJoin(out detail);
                case "ai_remove":
                    return PressAiRemove(out detail);
                default:
                    return PressButton(option, out detail);
            }
        }
        catch (Exception ex)
        {
            detail = "Menu press failed: " + ex.Message;
            return false;
        }
    }

    /// <summary>Leaves the open submenu (its Back button, else a stack pop). False at the root. 返回上一级。</summary>
    public static bool TryGoBack(out string detail)
    {
        detail = "";
        try
        {
            var mainMenu = NGame.Instance?.MainMenu;
            if (mainMenu == null || !GodotObject.IsInstanceValid(mainMenu))
            {
                detail = "The main menu is not open.";
                return false;
            }

            var top = PeekSubmenu(mainMenu);
            if (top == null)
            {
                detail = "Already at the main menu.";
                return false;
            }

            var back = FindBackButton(top);
            if (back != null && GodotObject.IsInstanceValid(back) && back.IsVisibleInTree() && back.IsEnabled)
            {
                back.ForceClick();
                detail = "Pressed the Back button.";
                return true;
            }

            mainMenu.SubmenuStack.Pop();
            detail = "Left the menu screen.";
            return true;
        }
        catch (Exception ex)
        {
            detail = "Could not leave the menu screen: " + ex.Message;
            return false;
        }
    }

    // ---- Root menu ---------------------------------------------------------------------------

    /// <summary>
    /// The root button list (Continue / Singleplayer / Multiplayer / ...). The legacy
    /// <c>menu.characters</c> data stays in the snapshot so the plugin can still offer
    /// <c>sts2_start_run</c>.
    /// </summary>
    private static void FillRoot(AgentMenuCapture capture, NMainMenu mainMenu)
    {
        capture.Phase = "menu";
        capture.Screen = "main";
        capture.Title = "Main menu";
        capture.Depth = 0;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        WalkRoot(mainMenu, capture.Options, seen, 0, ref truncated);

        capture.Menu = BuildRootMenu();
        capture.Actionable = capture.Options.Any(option => option.Enabled);
        capture.BlockedBy = capture.Actionable ? "" : "menu";

        // Starting/continuing a run stays a control tool, but every button is pressable now.
        capture.Notes.Add("Press a menu button with sts2_choose {index}. Start a solo run with "
                          + "sts2_start_run {mode,character,ascension,seed,modifiers}, or host a multiplayer "
                          + "lobby with sts2_host {mode}.");
        if (truncated)
            capture.Notes.Add($"Only the first {MaxOptions} options are listed.");
        if (!capture.Actionable)
            capture.Notes.Add("No main-menu button can be pressed right now; call sts2_wait.");
    }

    /// <summary>Legacy <c>menu</c> object (characters/save/ascension) plus the new mode/modifier data.</summary>
    private static JsonObject BuildRootMenu()
    {
        var characters = SoloRunController.GetCharacterChoices();
        var charactersJson = new JsonArray();
        foreach (var (id, name) in characters)
            charactersJson.Add(new JsonObject { ["id"] = id, ["name"] = name });

        var canContinue = false;
        var saveDetail = "no saved run";
        try
        {
            canContinue = SaveManager.Instance?.HasRunSave == true;
            if (canContinue)
                saveDetail = "a saved singleplayer run exists";
        }
        catch (Exception ex)
        {
            saveDetail = ex.Message;
        }

        return new JsonObject
        {
            ["screen"] = "main",
            ["title"] = "Main menu",
            ["can_start_run"] = characters.Count > 0 && SoloRunController.CanStartNewRun,
            ["can_continue"] = canContinue,
            ["max_ascension"] = SoloRunController.MaxAscensionLevel,
            ["characters"] = charactersJson,
            ["save"] = saveDetail,
            ["modes"] = new JsonArray(
                JsonValue.Create("standard"),
                JsonValue.Create("daily"),
                JsonValue.Create("custom")),
            ["can_start_daily"] = EpochRevealed<DailyRunEpoch>(),
            ["can_start_custom"] = EpochRevealed<CustomAndSeedsEpoch>(),
            ["modifiers"] = ModifiersJson()
        };
    }

    /// <summary>Good/bad run modifiers the agent may pass to <c>sts2_start_run</c> / custom lobbies.</summary>
    private static JsonArray ModifiersJson()
    {
        var array = new JsonArray();
        try
        {
            AppendModifiers(array, ModelDb.GoodModifiers, "good");
            AppendModifiers(array, ModelDb.BadModifiers, "bad");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Agent] modifier list failed: {ex.Message}");
        }

        return array;
    }

    private static void AppendModifiers(JsonArray array, IEnumerable<ModifierModel> modifiers, string kind)
    {
        foreach (var modifier in modifiers)
        {
            array.Add(new JsonObject
            {
                ["id"] = modifier.Id.Entry,
                ["name"] = ModifierName(modifier),
                ["kind"] = kind
            });
        }
    }

    private static bool EpochRevealed<T>() where T : EpochModel
    {
        try
        {
            return SaveManager.Instance?.IsEpochRevealed<T>() == true;
        }
        catch
        {
            return false;
        }
    }

    private static void WalkRoot(Node node, List<AgentMenuOption> options, HashSet<string> seen, int depth, ref bool truncated)
    {
        if (!GodotObject.IsInstanceValid(node))
            return;
        if (depth > MaxDepth)
            return;
        if (options.Count >= MaxOptions)
        {
            truncated = true;
            return;
        }

        if (node is NMainMenuTextButton button
            && GodotObject.IsInstanceValid(button)
            && button.IsVisibleInTree()
            && button.IsEnabled)
        {
            AddOption(options, seen, new AgentMenuOption
            {
                Kind = "button",
                Id = IdFromNodeName(button.Name),
                Label = ButtonLabel(button),
                Enabled = true,
                Node = button
            });
        }

        foreach (Node child in node.GetChildren())
            WalkRoot(child, options, seen, depth + 1, ref truncated);
    }

    // ---- Submenus / lobbies ------------------------------------------------------------------

    private static void FillSubmenu(AgentMenuCapture capture, NMainMenu mainMenu, NSubmenu top)
    {
        StartRunLobby? lobby = null;
        var lobbyScreen = false;
        capture.Depth = 1;

        switch (top)
        {
            case NCharacterSelectScreen screen:
                capture.Phase = "char_select";
                capture.Screen = "char_select";
                capture.Title = "Character select";
                lobby = SafeLobby(() => screen.Lobby);
                lobbyScreen = true;
                break;
            case NDailyRunScreen daily:
                capture.Phase = "daily_lobby";
                capture.Screen = "daily_lobby";
                capture.Title = "Daily run lobby";
                lobby = SafeLobby(() => DailyLobbyField?.GetValue(daily) as StartRunLobby);
                lobbyScreen = true;
                break;
            case NCustomRunScreen custom:
                capture.Phase = "custom_lobby";
                capture.Screen = "custom_lobby";
                capture.Title = "Custom run lobby";
                lobby = SafeLobby(() => custom.Lobby);
                lobbyScreen = true;
                break;
            case NSingleplayerSubmenu:
                capture.Phase = "menu";
                capture.Screen = "solo";
                capture.Title = "Singleplayer";
                break;
            case NMultiplayerSubmenu:
                capture.Phase = "menu";
                capture.Screen = "multiplayer";
                capture.Title = "Multiplayer";
                break;
            case NMultiplayerHostSubmenu:
                capture.Phase = "menu";
                capture.Screen = "host";
                capture.Title = "Host a multiplayer lobby";
                capture.Depth = 2;
                break;
            case NJoinFriendScreen:
                capture.Phase = "menu";
                capture.Screen = "join_friend";
                capture.Title = "Join a friend";
                break;
            default:
                capture.Phase = "menu";
                capture.Screen = "other";
                capture.Title = top.GetType().Name;
                break;
        }

        // A lobby pushed by the host submenu sits one level deeper than a standalone screen.
        if (lobbyScreen && StackContainsHostSubmenu(mainMenu))
            capture.Depth = 2;

        capture.Menu["screen"] = capture.Screen;
        capture.Menu["title"] = capture.Title;

        // The options array is the contract: enumerate before anything else can bail out.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        WalkScreen(top, capture.Options, seen, 0, ref truncated);

        if (lobbyScreen)
        {
            if (lobby == null)
            {
                capture.Menu["lobby"] = null;
                capture.Actionable = false;
                capture.BlockedBy = "lobby_loading";
                capture.Notes.Add(capture.Phase == "daily_lobby"
                    ? "The daily lobby is still fetching server time; call sts2_wait."
                    : "The lobby is still loading; call sts2_wait.");
            }
            else
            {
                capture.Menu["lobby"] = LobbyJson(lobby);
                AppendAiOptions(capture, lobby, capture.Phase == "char_select", seen);

                // The character-select screen is always driveable: the AI options above work even
                // when every vanilla button is disabled (for example while the player is ready).
                capture.Actionable = capture.Phase == "char_select"
                                     || capture.Options.Any(option => option.Enabled);
                if (!capture.Actionable)
                    capture.BlockedBy = "menu";
            }
        }
        else
        {
            capture.Actionable = capture.Options.Any(option => option.Enabled);
            if (!capture.Actionable)
                capture.BlockedBy = "menu";
        }

        if (truncated)
            capture.Notes.Add($"Only the first {MaxOptions} options are listed.");

        AppendScreenNote(capture);
    }

    private static void AppendScreenNote(AgentMenuCapture capture)
    {
        if (!capture.Actionable)
        {
            if (capture.BlockedBy is "" or "menu")
                capture.Notes.Add("Nothing on this screen can be pressed right now; call sts2_wait.");
            return;
        }

        switch (capture.Phase)
        {
            case "char_select":
                capture.Notes.Add("Pick a character with sts2_choose index=<n>, then press the Confirm button the "
                                  + "same way to embark. Add and ready the AI teammate, or remove it, with the "
                                  + "add_ai / remove_ai options. Leave the screen with sts2_proceed.");
                break;
            case "daily_lobby":
            case "custom_lobby":
                capture.Notes.Add("Press a lobby button with sts2_choose index=<n>; leave the lobby with sts2_proceed. "
                                  + "The AI teammate is added and readied automatically; change its character with the "
                                  + "panel's change-character button, then press one of the ai_character options.");
                break;
            default:
                capture.Notes.Add("Press a menu button with sts2_choose index=<n>; leave this screen with sts2_proceed.");
                break;
        }
    }

    /// <summary>Lobby details for char_select / daily_lobby / custom_lobby.</summary>
    private static JsonObject LobbyJson(StartRunLobby lobby)
    {
        var netService = lobby.NetService;
        var netType = netService?.Type ?? NetGameType.None;
        var localId = netService?.NetId ?? 0UL;

        var players = new JsonArray();
        JsonObject? local = null;
        foreach (var player in lobby.Players)
        {
            var entry = PlayerJson(player, localId);
            players.Add(entry);
            if (player.id == localId)
                local = entry.DeepClone() as JsonObject;
        }

        var manager = AiTeammateManager.Instance;
        bool aiInLobby;
        try
        {
            aiInLobby = manager.IsAiInLobby(lobby);
        }
        catch
        {
            aiInLobby = false;
        }

        var canBegin = false;
        try
        {
            canBegin = lobby.IsAboutToBeginGame();
        }
        catch
        {
            // Not fatal: the lobby just opened.
        }

        var dailyTime = lobby.DailyTime;

        return new JsonObject
        {
            ["mode"] = lobby.GameMode.ToString(),
            ["host"] = netType == NetGameType.Host,
            ["singleplayer"] = netType == NetGameType.Singleplayer,
            ["players"] = players,
            ["local"] = local,
            ["ai_in_lobby"] = aiInLobby,
            ["can_begin"] = canBegin,
            ["ascension"] = lobby.Ascension,
            ["seed"] = lobby.Seed,
            ["daily_time"] = dailyTime.HasValue ? dailyTime.Value.serverTime.ToString("o") : null
        };
    }

    private static JsonObject PlayerJson(LobbyPlayer player, ulong localId)
    {
        var isLocal = player.id == localId;
        var isAi = AiIdentity.IsAi(player.id);
        var characterName = CharacterName(player.character);

        // Players have no name field in the lobby; the display name is player-facing anyway.
        var name = isAi ? AiNames.DisplayName : isLocal ? "you" : characterName;

        return new JsonObject
        {
            ["name"] = name,
            ["character"] = characterName,
            ["character_id"] = player.character?.Id.Entry ?? "",
            ["ready"] = player.isReady,
            ["is_ai"] = isAi,
            ["is_local"] = isLocal
        };
    }

    /// <summary>The mod's synthetic AI teammate actions, appended after the vanilla options.</summary>
    private static void AppendAiOptions(AgentMenuCapture capture, StartRunLobby lobby, bool charSelect, HashSet<string> seen)
    {
        if (!charSelect)
            return;

        var manager = AiTeammateManager.Instance;
        bool aiInLobby;
        try
        {
            aiInLobby = manager.IsAiInLobby(lobby);
        }
        catch
        {
            aiInLobby = false;
        }

        if (!aiInLobby)
        {
            var capacity = 4;
            try
            {
                capacity = GameCompat.MaxPlayers(lobby);
            }
            catch
            {
                // Keep the fallback capacity: AddAiToLobby reports the real failure when pressed.
            }

            AddOption(capture.Options, seen, new AgentMenuOption
            {
                Kind = "ai_join",
                Id = "add_ai",
                // The button also readies the AI, so the label says so (unlike the mod's add button).
                Label = AiLoc.T("charselect.add_ai", "Add AI") + AiLoc.Pick("（并准备）", " (ready)"),
                Enabled = lobby.Players.Count < capacity,
                Node = null
            });
            return;
        }

        AddOption(capture.Options, seen, new AgentMenuOption
        {
            Kind = "ai_remove",
            Id = "remove_ai",
            Label = AiLoc.T("charselect.remove", "Remove") + " AI",
            Enabled = true,
            Node = null
        });
    }

    // ---- Option enumeration ------------------------------------------------------------------

    private static void WalkScreen(Node node, List<AgentMenuOption> options, HashSet<string> seen, int depth, ref bool truncated)
    {
        if (!GodotObject.IsInstanceValid(node))
            return;
        if (depth > MaxDepth)
            return;
        if (options.Count >= MaxOptions)
        {
            truncated = true;
            return;
        }

        var option = DescribeNode(node);
        if (option != null)
            AddOption(options, seen, option);

        foreach (Node child in node.GetChildren())
            WalkScreen(child, options, seen, depth + 1, ref truncated);
    }

    /// <summary>
    /// Classifies one menu node. Order matters: character buttons and tickboxes are also
    /// <see cref="NClickableControl"/>s, and every game button is also a Godot <see cref="Control"/>.
    /// </summary>
    private static AgentMenuOption? DescribeNode(Node node)
    {
        if (node is NCharacterSelectButton characterButton)
        {
            if (!characterButton.IsVisibleInTree())
                return null;

            var character = characterButton.Character;
            var characterId = character?.Id.Entry ?? "";

            // The daily/custom lobby AI picker reuses the very same character buttons; tag them so
            // the agent cannot confuse "this picks the AI's character" with the human's own pick
            // (and so the option ids do not collide with the custom lobby's own buttons).
            if (IsInsideAiPicker(characterButton))
            {
                return new AgentMenuOption
                {
                    Kind = "ai_character",
                    Id = characterId.Length > 0 ? "ai:" + characterId : "ai:random",
                    Label = AiLoc.T("lobbyai.option", "AI ← {Name}", ("Name", CharacterName(character))),
                    Enabled = characterButton.IsEnabled && !characterButton.IsLocked,
                    Node = characterButton
                };
            }

            return new AgentMenuOption
            {
                Kind = "character",
                Id = characterId.Length > 0 ? characterId : "random",
                Label = character is RandomCharacter
                    ? AiLoc.T("charselect.random", AiLoc.Pick("随机", "Random"))
                    : CharacterName(character),
                // Locked characters stay visible so the agent can see them, but are not pressable.
                Enabled = characterButton.IsEnabled && !characterButton.IsLocked,
                Node = characterButton
            };
        }

        // Only the custom-run tickboxes carry a ModifierModel; other tickboxes (settings) are
        // exposed as plain buttons by the NClickableControl branch below.
        if (node is NRunModifierTickbox modifierTickbox && modifierTickbox.Modifier is { } modifier)
        {
            if (!modifierTickbox.IsVisibleInTree())
                return null;

            return new AgentMenuOption
            {
                Kind = "modifier",
                Id = ModifierId(modifier),
                Label = FirstLabelText(modifierTickbox, includeRichText: false) ?? ModifierName(modifier),
                Enabled = modifierTickbox.IsEnabled,
                Ticked = modifierTickbox.IsTicked,
                Node = modifierTickbox
            };
        }

        if (node is NClickableControl clickable)
        {
            if (!clickable.IsVisibleInTree() || !clickable.IsEnabled)
                return null;

            return new AgentMenuOption
            {
                Kind = "button",
                Id = IdFromNodeName(clickable.Name),
                Label = FirstLabelText(clickable, includeRichText: true) ?? NodeLabel(clickable),
                Enabled = true,
                Node = clickable
            };
        }

        if (node is BaseButton baseButton)
        {
            if (!baseButton.IsVisibleInTree() || baseButton.Disabled)
                return null;

            var text = baseButton is Button button ? PromptTextHelper.CleanText(button.Text) : "";
            if (text.Length == 0)
            {
                // Icon-only blockers ("backstop" overlays) and code-created buttons with no text
                // cannot be described; auto names ("@Button@12") would only be noise.
                var nodeName = baseButton.Name.ToString();
                if (nodeName.Contains("backstop", StringComparison.OrdinalIgnoreCase)
                    || nodeName.StartsWith('@'))
                    return null;
                text = NodeLabel(baseButton);
            }

            return new AgentMenuOption
            {
                Kind = "button",
                Id = baseButton.Name.ToString().StartsWith('@')
                    ? SanitizeId(text)
                    : IdFromNodeName(baseButton.Name),
                Label = text,
                Enabled = true,
                Node = baseButton
            };
        }

        return null;
    }

    /// <summary>True when a node lives inside the daily/custom lobby AI character picker overlay.</summary>
    private static bool IsInsideAiPicker(Node node)
    {
        for (var parent = node.GetParent(); parent != null; parent = parent.GetParent())
        {
            if (string.Equals(parent.Name, LobbyAiCharacterPanel.PickerNodeName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool AddOption(List<AgentMenuOption> options, HashSet<string> seen, AgentMenuOption option)
    {
        if (options.Count >= MaxOptions)
            return false;
        if (!seen.Add(option.Kind + ":" + option.Id))
            return true;

        option.Index = options.Count;
        options.Add(option);
        return true;
    }

    // ---- Pressing ----------------------------------------------------------------------------

    private static bool PressCharacter(AgentMenuOption option, out string detail)
    {
        if (option.Node is not NCharacterSelectButton button || !GodotObject.IsInstanceValid(button))
        {
            detail = "That character button is no longer available.";
            return false;
        }

        button.Select();
        detail = $"Selected character [{option.Label}].";
        return true;
    }

    private static bool PressAiCharacter(AgentMenuOption option, out string detail)
    {
        if (option.Node is not NCharacterSelectButton button || !GodotObject.IsInstanceValid(button))
        {
            detail = "That AI character button is no longer available.";
            return false;
        }

        // Select() routes through the picker's ICharacterSelectButtonDelegate, which applies the
        // character to the AI lobby entry and closes the picker.
        button.Select();
        detail = $"Set the AI teammate's character to [{option.Label}].";
        return true;
    }

    private static bool PressModifier(AgentMenuOption option, out string detail)
    {
        if (option.Node is not NTickbox tickbox || !GodotObject.IsInstanceValid(tickbox))
        {
            detail = "That modifier tickbox is no longer available.";
            return false;
        }

        // NTickbox.OnRelease flips the state; ForceClick runs it without hover/focus checks.
        var willTick = !tickbox.IsTicked;
        tickbox.ForceClick();
        detail = $"{(willTick ? "Ticked" : "Unticked")} modifier [{option.Label}].";
        return true;
    }

    private static bool PressAiJoin(out string detail)
    {
        detail = "";
        var screen = PeekSubmenu(NGame.Instance?.MainMenu) as NCharacterSelectScreen;
        var lobby = screen?.Lobby;
        if (screen == null || lobby == null)
        {
            detail = "The character select lobby is not open.";
            return false;
        }

        var manager = AiTeammateManager.Instance;
        var added = manager.AddAiToLobby(lobby);
        if (!added.HasValue)
        {
            detail = "Could not add the AI teammate (the lobby is full or the mod is not initialized).";
            return false;
        }

        screen.PlayerConnected(added.Value);
        manager.SetAiReady(lobby, true);
        var again = manager.GetAiLobbyPlayer(lobby);
        if (again.HasValue)
            screen.PlayerChanged(again.Value, false);

        detail = $"Added and readied the AI teammate ({AiNames.DisplayName}).";
        return true;
    }

    private static bool PressAiRemove(out string detail)
    {
        detail = "";
        var screen = PeekSubmenu(NGame.Instance?.MainMenu) as NCharacterSelectScreen;
        var lobby = screen?.Lobby;
        if (screen == null || lobby == null)
        {
            detail = "The character select lobby is not open.";
            return false;
        }

        var manager = AiTeammateManager.Instance;
        var ai = manager.GetAiLobbyPlayer(lobby);
        if (!ai.HasValue)
        {
            detail = "The AI teammate is not in this lobby.";
            return false;
        }

        manager.RemoveAiFromLobby(lobby);
        screen.RemotePlayerDisconnected(ai.Value);
        detail = "Removed the AI teammate from the lobby.";
        return true;
    }

    private static bool PressButton(AgentMenuOption option, out string detail)
    {
        if (option.Node is NClickableControl clickable && GodotObject.IsInstanceValid(clickable))
        {
            clickable.ForceClick();
            detail = $"Pressed [{option.Label}].";
            return true;
        }

        // Plain Godot buttons (the mod's own "Add AI" button is one) only emit Pressed.
        if (option.Node is BaseButton button && GodotObject.IsInstanceValid(button))
        {
            button.EmitSignal(BaseButton.SignalName.Pressed);
            detail = $"Pressed [{option.Label}].";
            return true;
        }

        detail = "That button is no longer available.";
        return false;
    }

    // ---- Menu plumbing -----------------------------------------------------------------------

    private static NSubmenu? PeekSubmenu(NMainMenu? mainMenu)
    {
        try
        {
            var stack = mainMenu?.SubmenuStack;
            if (stack == null || !GodotObject.IsInstanceValid(stack))
                return null;
            return stack.Peek();
        }
        catch
        {
            return null;
        }
    }

    private static NBackButton? FindBackButton(Node root)
    {
        try
        {
            var direct = root.GetNodeOrNull<NBackButton>("BackButton");
            if (direct != null && GodotObject.IsInstanceValid(direct))
                return direct;
        }
        catch
        {
            // Fall through to a name search: the node cache may be stale during a transition.
        }

        return FindNamedBackButton(root);
    }

    private static NBackButton? FindNamedBackButton(Node node)
    {
        if (!GodotObject.IsInstanceValid(node))
            return null;
        if (node is NBackButton back && string.Equals(back.Name.ToString(), "BackButton", StringComparison.Ordinal))
            return back;

        foreach (Node child in node.GetChildren())
        {
            var found = FindNamedBackButton(child);
            if (found != null)
                return found;
        }

        return null;
    }

    /// <summary>Best effort: the host submenu sits below the lobby screen it pushed.</summary>
    private static bool StackContainsHostSubmenu(NMainMenu? mainMenu)
    {
        try
        {
            var stack = mainMenu?.SubmenuStack;
            if (stack == null || !GodotObject.IsInstanceValid(stack))
                return false;
            if (SubmenuStackField?.GetValue(stack) is not IEnumerable<NSubmenu> submenus)
                return false;

            foreach (var submenu in submenus)
            {
                if (submenu is NMultiplayerHostSubmenu)
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static StartRunLobby? SafeLobby(Func<StartRunLobby?> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeModalName()
    {
        try
        {
            var modal = NModalContainer.Instance?.OpenModal;
            return modal == null ? null : modal.GetType().Name;
        }
        catch
        {
            return null;
        }
    }

    // ---- Text helpers ------------------------------------------------------------------------

    private static string ButtonLabel(NMainMenuTextButton button)
    {
        var text = PromptTextHelper.CleanText(button.label?.Text);
        var label = text.Length > 0 ? text : NodeLabel(button);
        return PromptTextHelper.Truncate(label, MaxLabelLength);
    }

    /// <summary>
    /// Display text under a node: the first plain <see cref="Label"/> wins (buttons put their title
    /// there), rich text is only a fallback because it usually holds long descriptions.
    /// </summary>
    private static string? FirstLabelText(Node root, bool includeRichText)
    {
        var plain = FindLabelText(root, rich: false);
        if (plain != null)
            return plain;
        return includeRichText ? FindLabelText(root, rich: true) : null;
    }

    private static string? FindLabelText(Node node, bool rich)
    {
        if (!GodotObject.IsInstanceValid(node))
            return null;

        if (rich)
        {
            if (node is RichTextLabel richLabel && !string.IsNullOrWhiteSpace(richLabel.Text))
            {
                var text = PromptTextHelper.CleanText(richLabel.Text);
                if (text.Length > 0)
                    return PromptTextHelper.Truncate(text, MaxLabelLength);
            }
        }
        else if (node is Label label && !string.IsNullOrWhiteSpace(label.Text))
        {
            var text = PromptTextHelper.CleanText(label.Text);
            if (text.Length > 0)
                return PromptTextHelper.Truncate(text, MaxLabelLength);
        }

        foreach (Node child in node.GetChildren())
        {
            var found = FindLabelText(child, rich);
            if (found != null)
                return found;
        }

        return null;
    }

    private static string NodeLabel(Node node)
    {
        var name = node.Name.ToString();
        if (name.EndsWith("Button", StringComparison.OrdinalIgnoreCase))
            name = name[..^"Button".Length];

        var cleaned = PromptTextHelper.CleanText(name.Replace('_', ' '));
        var label = cleaned.Length > 0 ? cleaned : node.Name.ToString();
        return PromptTextHelper.Truncate(label, MaxLabelLength);
    }

    /// <summary>"SingleplayerButton" -> "singleplayer". Stable across frames; used by the plugin.</summary>
    private static string IdFromNodeName(StringName name)
    {
        var text = name.ToString();
        if (text.EndsWith("Button", StringComparison.OrdinalIgnoreCase))
            text = text[..^"Button".Length];

        var id = SanitizeId(text);
        return id.Length > 0 ? id : "button";
    }

    private static string SanitizeId(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));
            else if (builder.Length > 0 && builder[^1] != '_')
                builder.Append('_');
        }

        return builder.ToString().Trim('_');
    }

    private static string CharacterName(CharacterModel? character)
    {
        try
        {
            return PromptTextHelper.ResolveLocString(character?.Title, character?.Id.Entry ?? "?");
        }
        catch
        {
            return character?.Id.Entry ?? "?";
        }
    }

    private static string ModifierName(ModifierModel modifier)
    {
        try
        {
            return PromptTextHelper.ResolveLocString(modifier.Title, modifier.Id.Entry);
        }
        catch
        {
            // Canonical CharacterCards has no character set, so its title getter throws.
            return modifier.Id.Entry;
        }
    }

    /// <summary>Modifier id; CharacterCards is per-character, so its character id is appended.</summary>
    private static string ModifierId(ModifierModel modifier)
    {
        var id = modifier.Id.Entry;
        if (modifier is not CharacterCards characterCards)
            return id;

        try
        {
            var characterId = characterCards.CharacterModel.Entry;
            if (!string.IsNullOrEmpty(characterId))
                id += ":" + characterId;
        }
        catch
        {
            // Canonical CharacterCards without a character: keep the bare id.
        }

        return id;
    }
}
