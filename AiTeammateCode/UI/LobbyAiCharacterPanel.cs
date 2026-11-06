using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Localization;

namespace AiTeammate.AiTeammateCode.Ui;

/// <summary>Which lobby screen the card is parked on (they have different free space).</summary>
public enum LobbyAiScreenKind
{
    Daily,
    Custom
}

/// <summary>
/// Compact "AI teammate" card for the daily / custom multiplayer lobby screens.
///
/// Those two screens have no character picker at all (the daily character is rolled by the seed and
/// the custom screen's buttons belong to the human), so the AI teammate was auto-added
/// (<see cref="Patches.DailyCustomLobbyAiPatch"/>) with whatever character
/// <see cref="AiTeammateManager.AddAiToLobby"/> picked and could never be changed. The standard
/// character-select screen keeps its full AI panel (<see cref="Patches.CharacterSelectScreenPatch"/>);
/// this card is the same idea in a smaller form: show who the AI is, and open a character grid.
///
/// 每日挑战 / 自定义模式下 AI 队友是自动加入的，那两个界面没有自己的角色选择器，
/// 所以 AI 的角色没法调整；这里补一个可展开的 AI 卡片。
/// </summary>
public partial class LobbyAiCharacterPanel : PanelContainer
{
    public const string PanelNodeName = "__AiTeammateLobbyPanel";
    public const string PickerNodeName = "__AiTeammateLobbyPicker";

    /// <summary>Scene of the vanilla character button, reused so the picker looks native.</summary>
    internal const string CharacterButtonScenePath = "res://scenes/screens/char_select/char_select_button.tscn";

    // Free-space anchors measured against the vanilla scenes (1920x1080 virtual canvas):
    //  - daily_run_screen: the mod's chat panel covers x≈19..441 / y≈486..864, the remote player box
    //    ends at y≈572 and the modifiers list starts at y≈696 on the left half → park at (457, 580).
    //  - custom_run_screen: the top-left corner above the title text (text starts at x≈359) and
    //    above the remote player box is empty → park at (24, 24).
    // The card is a narrow vertical stack so it fits both gaps.
    private static readonly Vector2 DailyPosition = new(457f, 580f);
    private static readonly Vector2 CustomPosition = new(24f, 24f);

    private const double RefreshIntervalSeconds = 0.25;

    private Func<StartRunLobby?>? _lobbyAccessor;
    private TextureRect _icon = null!;
    private Label _nameLabel = null!;
    private Label _statusLabel = null!;
    private double _refreshTimer;
    private bool _warned;

    public LobbyAiCharacterPanel()
    {
        Name = PanelNodeName;
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(226f, 0f);

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.07f, 0.08f, 0.1f, 0.9f),
            BorderColor = new Color(0.85f, 0.72f, 0.3f, 0.65f),
            ContentMarginLeft = 10f,
            ContentMarginRight = 10f,
            ContentMarginTop = 8f,
            ContentMarginBottom = 8f
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(6);
        AddThemeStyleboxOverride("panel", style);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        AddChild(vbox);

        var title = new Label { Text = AiLoc.T("lobbyai.title", "AI teammate") };
        title.AddThemeColorOverride("font_color", new Color(0.94f, 0.78f, 0.32f));
        title.AddThemeFontSizeOverride("font_size", 14);
        vbox.AddChild(title);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        vbox.AddChild(row);

        _icon = new TextureRect
        {
            CustomMinimumSize = new Vector2(40f, 40f),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        row.AddChild(_icon);

        var textBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        textBox.AddThemeConstantOverride("separation", 2);
        row.AddChild(textBox);

        _nameLabel = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _nameLabel.AddThemeFontSizeOverride("font_size", 15);
        textBox.AddChild(_nameLabel);

        _statusLabel = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _statusLabel.AddThemeFontSizeOverride("font_size", 12);
        _statusLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f));
        textBox.AddChild(_statusLabel);

        var changeButton = new Button
        {
            Text = AiLoc.T("lobbyai.change", "Change character"),
            CustomMinimumSize = new Vector2(0f, 32f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        changeButton.AddThemeFontSizeOverride("font_size", 13);
        changeButton.Pressed += OnChangePressed;
        // Below the character line (not beside it) so the card stays narrow enough for the free
        // corners of both lobby layouts.
        vbox.AddChild(changeButton);
    }

    /// <summary>
    /// Adds the card to a lobby screen (idempotent: a reused screen keeps the one card). The
    /// accessor is polled because a hosted daily lobby only exists after the time-server fetch.
    /// </summary>
    public static void Attach(NSubmenu screen, Func<StartRunLobby?> lobbyAccessor, LobbyAiScreenKind kind)
    {
        try
        {
            var existing = screen.GetNodeOrNull<LobbyAiCharacterPanel>(PanelNodeName);
            if (existing != null)
            {
                existing._lobbyAccessor = lobbyAccessor;
                existing.Refresh();
                return;
            }

            var panel = new LobbyAiCharacterPanel
            {
                _lobbyAccessor = lobbyAccessor,
                Position = kind == LobbyAiScreenKind.Daily ? DailyPosition : CustomPosition,
                Visible = false
            };

            screen.AddChild(panel);
            panel.Refresh();

            var remote = screen.GetNodeOrNull<Control>("RemotePlayerContainer")
                         ?? screen.FindChild("RemotePlayerContainer", recursive: true, owned: false) as Control;
            MainFile.Logger.Info(
                $"[Lobby] AI character card attached to {screen.GetType().Name} at {panel.Position}"
                + (remote != null ? $" (remote list {remote.Position}/{remote.Size})" : string.Empty));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Lobby] failed to attach the AI character card to {screen.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Closes the character picker if it is open on <paramref name="screen"/>; true when it handled the press.</summary>
    public static bool TryClosePicker(NSubmenu? screen)
    {
        if (screen == null || !GodotObject.IsInstanceValid(screen))
            return false;

        var picker = screen.GetNodeOrNull<AiCharacterPicker>(PickerNodeName);
        if (picker == null)
            return false;

        picker.QueueFree();
        return true;
    }

    public override void _Process(double delta)
    {
        _refreshTimer += delta;
        if (_refreshTimer < RefreshIntervalSeconds)
            return;

        _refreshTimer = 0d;
        Refresh();
    }

    private void Refresh()
    {
        try
        {
            var lobby = _lobbyAccessor?.Invoke();
            if (lobby?.NetService == null || lobby.NetService.Type != NetGameType.Host)
            {
                Visible = false;
                return;
            }

            var manager = AiTeammateManager.Instance;
            if (manager == null || !manager.IsInitialized)
            {
                Visible = false;
                return;
            }

            var ai = manager.GetAiLobbyPlayer(lobby);
            if (!ai.HasValue)
            {
                Visible = false;
                return;
            }

            _warned = false;
            Visible = true;

            var character = ai.Value.character;
            _icon.Texture = character?.IconTexture;
            var characterName = character?.Title.GetFormattedText() ?? string.Empty;
            _nameLabel.Text = AiNames.HasCustomNickname
                ? AiLoc.T("charselect.name_custom", "AI: {Nickname} ({Character})",
                    ("Nickname", AiNames.DisplayName), ("Character", characterName))
                : AiLoc.T("charselect.name", "AI: {Name}", ("Name", characterName));
            _statusLabel.Text = ai.Value.isReady
                ? AiLoc.T("charselect.status.ready", "Status: Ready")
                : AiLoc.T("charselect.status.not_ready", "Status: Not ready");
        }
        catch (Exception ex)
        {
            // Hidden on failure instead of a broken card; logged once per screen to avoid a 4/s spam.
            Visible = false;
            if (_warned)
                return;

            _warned = true;
            MainFile.Logger.Warn($"[Lobby] AI character card refresh failed: {ex.Message}");
        }
    }

    private void OnChangePressed()
    {
        try
        {
            var parent = GetParent();
            if (parent == null)
                return;
            if (parent.GetNodeOrNull<AiCharacterPicker>(PickerNodeName) != null)
                return;

            var lobby = _lobbyAccessor?.Invoke();
            if (lobby == null)
                return;

            var picker = new AiCharacterPicker(lobby, OnCharacterPicked) { Name = PickerNodeName };
            parent.AddChild(picker);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Lobby] failed to open the AI character picker: {ex.Message}");
        }
    }

    private void OnCharacterPicked(CharacterModel character)
    {
        try
        {
            var lobby = _lobbyAccessor?.Invoke();
            if (lobby == null)
                return;

            AiTeammateManager.Instance.SetAiCharacter(lobby, character);
            MainFile.Logger.Info($"[Lobby] AI teammate character set to {character.Id.Entry}");
            RefreshVanillaPlayerList(lobby);

            try
            {
                SfxCmd.Play(character.CharacterSelectSfx);
            }
            catch
            {
                // Sound is cosmetic; a missing clip must not lose the pick.
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Lobby] failed to set the AI teammate character: {ex.Message}");
        }
        finally
        {
            var parent = GetParent();
            parent?.GetNodeOrNull<AiCharacterPicker>(PickerNodeName)?.QueueFree();
            Refresh();
        }
    }

    /// <summary>
    /// Both screens build their remote-player rows once at lobby init, so the AI's row and (on the
    /// custom screen) its "picked by a remote player" marker keep the old character after a change.
    /// Re-deliver the changed lobby player so the vanilla UI matches the card.
    /// </summary>
    private void RefreshVanillaPlayerList(StartRunLobby lobby)
    {
        try
        {
            var ai = AiTeammateManager.Instance.GetAiLobbyPlayer(lobby);
            if (!ai.HasValue)
                return;

            switch (GetParent())
            {
                case MegaCrit.Sts2.Core.Nodes.Screens.DailyRun.NDailyRunScreen daily:
                    daily.PlayerChanged(ai.Value, isRandomCharacterResolution: false);
                    break;
                case MegaCrit.Sts2.Core.Nodes.Screens.CustomRun.NCustomRunScreen custom:
                    custom.PlayerChanged(ai.Value, isRandomCharacterResolution: false);
                    break;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Lobby] could not refresh the vanilla lobby player list: {ex.Message}");
        }
    }
}

/// <summary>
/// Full-screen character grid overlay. Reuses vanilla <see cref="NCharacterSelectButton"/>s so it
/// looks and behaves exactly like the character-select screen (including the per-character sfx).
/// </summary>
public partial class AiCharacterPicker : Control
{
    private readonly StartRunLobby _lobby;
    private readonly Action<CharacterModel> _onPicked;
    private bool _picked;
    private ulong _openedAtMsec;
    private Button? _closeButton;

    public AiCharacterPicker(StartRunLobby lobby, Action<CharacterModel> onPicked)
    {
        _lobby = lobby;
        _onPicked = onPicked;

        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        ZIndex = 100;
    }

    public override void _Ready()
    {
        // Children must be added while this node is inside the tree: NCharacterSelectButton.Init
        // reads nodes that its own _Ready creates, and _Ready only runs once a node is in the tree.
        BuildUi();
        _openedAtMsec = Time.GetTicksMsec();
    }

    private void BuildUi()
    {
        var dim = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.72f),
            MouseFilter = MouseFilterEnum.Stop
        };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Pass };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer();
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.09f, 0.11f, 0.98f),
            BorderColor = new Color(0.85f, 0.72f, 0.3f, 0.8f)
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(10);
        panel.AddThemeStyleboxOverride("panel", style);
        center.AddChild(panel);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        panel.AddChild(margin);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 12);
        margin.AddChild(vbox);

        var title = new Label { Text = AiLoc.T("lobbyai.picker_title", "Choose the AI teammate's character") };
        title.AddThemeFontSizeOverride("font_size", 26);
        title.AddThemeColorOverride("font_color", new Color(0.94f, 0.78f, 0.32f));
        vbox.AddChild(title);

        var hint = new Label
        {
            Text = AiLoc.T("lobbyai.picker_hint",
                "Click a character to apply it right away; press Back or Close to leave."),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(760f, 0f)
        };
        hint.AddThemeFontSizeOverride("font_size", 14);
        hint.AddThemeColorOverride("font_color", new Color(0.75f, 0.75f, 0.75f));
        vbox.AddChild(hint);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(760f, 360f),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        vbox.AddChild(scroll);

        var grid = new GridContainer
        {
            Columns = 5,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        grid.AddThemeConstantOverride("h_separation", 16);
        grid.AddThemeConstantOverride("v_separation", 16);
        scroll.AddChild(grid);

        var delegateObj = new CharacterPickerDelegate(_lobby, this);
        var characters = AiTeammateManager.Instance?.GetAiCharacterOptions() ?? new List<CharacterModel>();
        var buttonScene = LoadCharacterButtonScene();
        var added = 0;

        if (buttonScene != null)
        {
            foreach (var character in characters)
            {
                try
                {
                    var button = buttonScene.Instantiate<NCharacterSelectButton>(PackedScene.GenEditState.Disabled);
                    // Vanilla order: add to the tree first, then Init (Init reads nodes created in _Ready).
                    grid.AddChild(button);
                    button.Init(character, delegateObj);
                    if (button.IsLocked)
                    {
                        grid.RemoveChild(button);
                        button.QueueFree();
                        continue;
                    }

                    added++;
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Warn($"[Lobby] AI picker button for {character.Id.Entry} failed: {ex.Message}");
                }
            }
        }

        if (added == 0)
        {
            var empty = new Label { Text = AiLoc.T("lobbyai.empty", "No selectable character was found.") };
            empty.AddThemeFontSizeOverride("font_size", 16);
            scroll.AddChild(empty);
        }

        var footer = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        vbox.AddChild(footer);

        _closeButton = new Button
        {
            Text = AiLoc.T("lobbyai.close", "Close"),
            CustomMinimumSize = new Vector2(120f, 36f)
        };
        _closeButton.Pressed += QueueFree;
        footer.AddChild(_closeButton);

        // Keep focus off a character button: NCharacterSelectButton.Select() runs on FocusEntered,
        // so a stray focus grab (controller / a neighbor being freed) would pick a character before
        // the player even looked at the grid.
        _closeButton.CallDeferred(Control.MethodName.GrabFocus);
    }

    internal void OnCharacterSelected(CharacterModel character)
    {
        if (_picked)
            return;

        // A click/focus that resolves before the grid is even on screen is the opening press
        // bleeding through, not a choice.
        var elapsed = Time.GetTicksMsec() - _openedAtMsec;
        if (elapsed < 150)
        {
            MainFile.Logger.Info($"[Lobby] ignored an AI pick {elapsed}ms after opening ({character.Id.Entry})");
            return;
        }

        _picked = true;

        try
        {
            _onPicked(character);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Lobby] AI picker selection failed: {ex.Message}");
        }
    }

    internal static PackedScene? LoadCharacterButtonScene()
    {
        try
        {
            return MegaCrit.Sts2.Core.Assets.PreloadManager.Cache.GetScene(
                LobbyAiCharacterPanel.CharacterButtonScenePath);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Lobby] character button scene missing: {ex.Message}");
            return null;
        }
    }

    private sealed class CharacterPickerDelegate : ICharacterSelectButtonDelegate
    {
        private readonly AiCharacterPicker _owner;

        public CharacterPickerDelegate(StartRunLobby lobby, AiCharacterPicker owner)
        {
            Lobby = lobby;
            _owner = owner;
        }

        public StartRunLobby Lobby { get; }

        public void SelectCharacter(NCharacterSelectButton charSelectButton, CharacterModel characterModel)
        {
            _owner.OnCharacterSelected(characterModel);
        }
    }
}
