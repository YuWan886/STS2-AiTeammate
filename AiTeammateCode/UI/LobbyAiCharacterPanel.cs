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

/// <summary>
/// Compact "AI teammate" card for the lobby screens that have no character picker for the AI:
/// daily, custom, and (beta 0.113.1+) Dread and Blitz.
///
/// Those screens either roll the human's character from the seed or expose only the human's own
/// buttons, so the AI teammate was auto-added (<see cref="Patches.AutoLobbyAiPatch"/>) with whatever
/// character <see cref="AiTeammateManager.AddAiToLobby"/> picked and could never be changed. The
/// standard character-select screen keeps its full AI panel
/// (<see cref="Patches.CharacterSelectScreenPatch"/>); this card is the same idea in a smaller form:
/// show who the AI is, and open a character grid.
///
/// The card starts in the free top-left corner; <see cref="EnsureParked"/> moves it out of the way
/// once, for the new lobby screens whose remote-player list occupies that corner.
/// 每日/自定/Dread/Blitz 下 AI 队友是自动加入的，那些界面没有自己的角色选择器，
/// 所以 AI 的角色没法调整；这里补一个可展开的 AI 卡片。
/// </summary>
public partial class LobbyAiCharacterPanel : PanelContainer
{
    public const string PanelNodeName = "__AiTeammateLobbyPanel";
    public const string PickerNodeName = "__AiTeammateLobbyPicker";

    /// <summary>Scene of the vanilla character button, reused so the picker looks native.</summary>
    internal const string CharacterButtonScenePath = "res://scenes/screens/char_select/char_select_button.tscn";

    // Both lobby screens park the card in the same top-left corner (user request 2026-11-06): the
    // centered title text starts at x≈359 on both screens and the mod's chat panel covers the
    // bottom-left (x≈19..441 / y≈486..864), so the corner above/left of them is empty everywhere.
    private static readonly Vector2 PanelPosition = new(24f, 24f);

    private const double RefreshIntervalSeconds = 0.25;

    private Func<StartRunLobby?>? _lobbyAccessor;
    private TextureRect _icon = null!;
    private Label _nameLabel = null!;
    private Label _statusLabel = null!;
    private double _refreshTimer;
    private bool _warned;
    private bool _parked;

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
    public static void Attach(NSubmenu screen, Func<StartRunLobby?> lobbyAccessor)
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
                Position = PanelPosition,
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
            EnsureParked();

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

    /// <summary>
    /// Daily/custom leave the top-left corner free, but the Dread/Blitz lobbies put their
    /// remote-player list exactly there (Dread: 48,36 → 566,435), and the card would sit on top of
    /// it. Once per screen, move the card to the right of that list when it still fits on screen,
    /// otherwise just below it. 每日/自定左上角是空的；Dread/Blitz 的玩家列表就在左上角，这里把卡片挪开一次。
    /// </summary>
    private void EnsureParked()
    {
        if (_parked)
            return;
        if (GetParent() is not NSubmenu screen)
            return;

        try
        {
            var remote = screen.GetNodeOrNull<Control>("RemotePlayerContainer")
                         ?? screen.FindChild("RemotePlayerContainer", recursive: true, owned: false) as Control;
            if (remote == null || remote.Size.X <= 0f || remote.Size.Y <= 0f)
                return;

            var list = new Rect2(remote.GlobalPosition - screen.GlobalPosition, remote.Size);
            var card = new Rect2(Position, new Vector2(CustomMinimumSize.X, 150f));
            if (!list.Intersects(card))
            {
                _parked = true;
                return;
            }

            // Both new lobbies pin an info button (DreadInfoButton / BlitzInfoButton, 64x64 at a 24px
            // margin) to the top-right corner, so the card sits to the LEFT of it instead of covering
            // it. The bare top-right edge is used only when the button cannot be found; either way the
            // card drops below the player list on a screen too narrow to hold it.
            var viewport = GetViewportRect().Size;
            var info = FindInfoButton(screen);
            var rightEdge = info != null
                ? info.GlobalPosition.X - screen.GlobalPosition.X - 12f
                : viewport.X - PanelPosition.X;
            var cardX = Mathf.Max(PanelPosition.X, rightEdge - card.Size.X);
            Position = cardX > list.End.X + 12f
                ? new Vector2(cardX, PanelPosition.Y)
                : new Vector2(PanelPosition.X, list.End.Y + 12f);
            _parked = true;
            MainFile.Logger.Info(
                $"[Lobby] AI character card moved to {Position} to clear the player list of {screen.GetType().Name}");
        }
        catch (Exception ex)
        {
            _parked = true;
            MainFile.Logger.Warn($"[Lobby] could not park the AI character card: {ex.Message}");
        }
    }

    /// <summary>
    /// The "?" button both new lobbies pin to their top-right corner (DreadInfoButton /
    /// BlitzInfoButton, both instances of dread_info_button.tscn anchored to the right edge).
    /// 恐惧/极速大厅右上角的问号按钮，卡片要避开它。
    /// </summary>
    private static Control? FindInfoButton(NSubmenu screen)
    {
        foreach (var name in new[] { "%DreadInfoButton", "%BlitzInfoButton", "DreadInfoButton", "BlitzInfoButton" })
        {
            try
            {
                var node = screen.GetNodeOrNull<Control>(name);
                if (node != null && GodotObject.IsInstanceValid(node))
                    return node;
            }
            catch
            {
                // A missing node is the normal case for the other lobby screen.
            }
        }

        return null;
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
    /// Lobby screens build their remote-player rows once at lobby init, so the AI's row and (on the
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
                    GameCompat.NotifyPlayerChanged(daily, ai.Value);
                    break;
                case MegaCrit.Sts2.Core.Nodes.Screens.CustomRun.NCustomRunScreen custom:
                    GameCompat.NotifyPlayerChanged(custom, ai.Value);
                    break;
                default:
                    // Dread / Blitz (0.113.1+) implement the same PlayerChanged(LobbyPlayer) hook, but
                    // their types are not compiled into the older variants: the shim reflects on it.
                    if (GetParent() is NSubmenu other)
                        GameCompat.NotifyPlayerChanged(other, ai.Value);
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

#if STS2_API_0_113_OR_LATER
        public void SelectCharacter(NCharacterSelectButton charSelectButton)
        {
            if (charSelectButton?.Character is { } character)
                _owner.OnCharacterSelected(character);
        }
#else
        public void SelectCharacter(NCharacterSelectButton charSelectButton, CharacterModel characterModel)
        {
            _owner.OnCharacterSelected(characterModel);
        }
#endif
    }
}
