using System.Reflection;
using System.Linq;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Vfx.Utilities;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Localization;

namespace AiTeammate.AiTeammateCode.Patches;

/// <summary>
/// Adds manual AI lobby controls and reuses the native character-select flow
/// so the host can configure the AI character separately from their own.
/// </summary>
[HarmonyLib.HarmonyPatch(typeof(NCharacterSelectScreen), "AfterInitialized")]
public static class CharacterSelectScreenPatch
{
    private sealed class AiNodes
    {
        public Control Container = null!;
        public Button AddBtn = null!;
        public HBoxContainer CardRow = null!;
        public Button CardBtn = null!;
        public TextureRect Icon = null!;
        public Label NameLabel = null!;
        public Label StatusLabel = null!;
        public Button RemoveBtn = null!;
        public Label HintLabel = null!;
        public bool IsEditingAi;
    }

    private static readonly Dictionary<ulong, AiNodes> _screenNodes = new();

    private static readonly FieldInfo? SelectedButtonField = HarmonyLib.AccessTools.Field(typeof(NCharacterSelectScreen), "_selectedButton");
    private static readonly FieldInfo? InfoPanelTweenField = HarmonyLib.AccessTools.Field(typeof(NCharacterSelectScreen), "_infoPanelTween");
    private static readonly FieldInfo? InfoPanelPosFinalValField = HarmonyLib.AccessTools.Field(typeof(NCharacterSelectScreen), "_infoPanelPosFinalVal");
    private static readonly FieldInfo? IsSelectedField = HarmonyLib.AccessTools.Field(typeof(NCharacterSelectButton), "_isSelected");
    private static readonly MethodInfo? RefreshStateMethod = HarmonyLib.AccessTools.Method(typeof(NCharacterSelectButton), "RefreshState");

    private const string AiContainerName = "__AiTeammateContainer";

    [HarmonyLib.HarmonyPostfix]
    public static void AfterLobbyInitialized(NCharacterSelectScreen __instance)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsInitialized)
            return;

        var lobby = __instance.Lobby;
        if (lobby == null || Compat.GameCompat.MaxPlayers(lobby) <= 1)
            return;

        // Register the nickname sync while every client is in the lobby, so the host's first
        // broadcast at run start cannot race a client that has not registered yet.
        try
        {
            AiNicknameSync.EnsureRegistered(lobby.NetService);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Nickname] 大厅昵称同步注册失败: {ex.Message}");
        }

        var existing = __instance.GetNodeOrNull<Control>(AiContainerName);
        existing?.QueueFree();

        var nodes = BuildAiUi(__instance);
        nodes.Container.Name = AiContainerName;
        nodes.IsEditingAi = false;

        // The panel used to be a transparent, full-height column pinned to the screen's left edge.
        // That column sat right on top of RemotePlayerContainer — the node that owns the lobby's
        // vanilla "Invite" button — so the invisible overlay swallowed every click aimed at that
        // button. Park the panel beside the player list and make its whole layout ignore the mouse;
        // only the real buttons inside can take a click now.
        ParkBesidePlayerList(__instance, nodes.Container);

        __instance.AddChild(nodes.Container);
        _screenNodes[__instance.GetInstanceId()] = nodes;

        RefreshUi(nodes, lobby);
    }

    internal static bool IsEditingAi(NCharacterSelectScreen screen)
    {
        return _screenNodes.TryGetValue(screen.GetInstanceId(), out var nodes) && nodes.IsEditingAi;
    }

    internal static bool TryExitAiEditMode(NCharacterSelectScreen screen)
    {
        if (!_screenNodes.TryGetValue(screen.GetInstanceId(), out var nodes) || !nodes.IsEditingAi)
            return false;

        nodes.IsEditingAi = false;
        RefreshUi(nodes, screen.Lobby);
        ShowLobbyPlayerSelection(screen, screen.Lobby.LocalPlayer, isAiEdit: false);
        return true;
    }

    private static AiNodes BuildAiUi(NCharacterSelectScreen screen)
    {
        var n = new AiNodes();

        n.Container = new MarginContainer();
        n.Container.AddThemeConstantOverride("margin_left", 8);
        n.Container.AddThemeConstantOverride("margin_top", 6);

        // Every layout node stays mouse-transparent so only the real buttons below can take a click.
        // An invisible container that answers the mouse also eats clicks aimed at vanilla lobby UI
        // drawn underneath it (the "Invite" button used to be unreachable for exactly that reason).
        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 6);
        vbox.MouseFilter = Control.MouseFilterEnum.Ignore;
        n.Container.AddChild(vbox);

        n.AddBtn = new Button();
        n.AddBtn.Text = AiLoc.T("charselect.add_ai", "Add AI");
        n.AddBtn.CustomMinimumSize = new Vector2(180, 36);
        n.AddBtn.Pressed += () => OnAddAiClicked(screen);
        vbox.AddChild(n.AddBtn);

        n.CardRow = new HBoxContainer();
        n.CardRow.AddThemeConstantOverride("separation", 6);
        n.CardRow.MouseFilter = Control.MouseFilterEnum.Ignore;
        vbox.AddChild(n.CardRow);

        n.CardBtn = new Button();
        n.CardBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        n.CardBtn.CustomMinimumSize = new Vector2(220, 52);
        n.CardBtn.Text = string.Empty;
        n.CardBtn.Pressed += () => EnterAiEditMode(screen);
        n.CardRow.AddChild(n.CardBtn);

        var cardHbox = new HBoxContainer();
        cardHbox.AddThemeConstantOverride("separation", 8);
        cardHbox.MouseFilter = Control.MouseFilterEnum.Ignore;
        n.CardBtn.AddChild(cardHbox);

        n.Icon = new TextureRect();
        n.Icon.CustomMinimumSize = new Vector2(40, 40);
        n.Icon.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        n.Icon.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
        n.Icon.MouseFilter = Control.MouseFilterEnum.Ignore;
        cardHbox.AddChild(n.Icon);

        var cardTextVbox = new VBoxContainer();
        cardTextVbox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        cardTextVbox.MouseFilter = Control.MouseFilterEnum.Ignore;
        cardHbox.AddChild(cardTextVbox);

        n.NameLabel = new Label();
        n.NameLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        cardTextVbox.AddChild(n.NameLabel);

        n.StatusLabel = new Label();
        n.StatusLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        cardTextVbox.AddChild(n.StatusLabel);

        n.RemoveBtn = new Button();
        n.RemoveBtn.Text = AiLoc.T("charselect.remove", "Remove");
        n.RemoveBtn.CustomMinimumSize = new Vector2(72, 52);
        n.RemoveBtn.Pressed += () => OnRemoveAiClicked(screen);
        n.CardRow.AddChild(n.RemoveBtn);

        n.HintLabel = new Label();
        n.HintLabel.Text = AiLoc.T("charselect.hint",
            "Editing the AI teammate: pick a character, then confirm to ready the AI; press Back to return to your own character.");
        n.HintLabel.Visible = false;
        n.HintLabel.MouseFilter = Control.MouseFilterEnum.Ignore;
        // Wrap inside the panel instead of stretching it: the hint is one long sentence and used to
        // widen the whole panel to ~600px.
        n.HintLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        n.HintLabel.CustomMinimumSize = new Vector2(PanelWidth - 16f, 0f);
        n.HintLabel.AddThemeFontSizeOverride("font_size", 13);
        vbox.AddChild(n.HintLabel);

        return n;
    }

    // Panel footprint next to the vanilla lobby player list. The character-select layout is empty
    // there (the list ends at x≈554, InfoPanel starts at y≈345 further right), so the panel can
    // never overlap anything the player needs to click.
    private const float PanelWidth = 310f;
    private const float PanelHeight = 170f;
    private const float PanelGap = 16f;

    /// <summary>
    /// Parks the AI panel beside the vanilla lobby player list (<c>RemotePlayerContainer</c>)
    /// instead of on top of it. That node hosts the "Invite" button, and a transparent full-height
    /// overlay sitting on top of it silently swallowed every click aimed at the button.
    /// </summary>
    private static void ParkBesidePlayerList(NCharacterSelectScreen screen, Control panel)
    {
        // Fallback mirrors the vanilla scene rect: RemotePlayerContainer at (36, 45), size 518x354.
        float left = 554f + PanelGap;
        float top = 45f;

        var playerList = screen.GetNodeOrNull<Control>("RemotePlayerContainer");
        if (playerList != null)
        {
            Vector2 position = playerList.Position;
            Vector2 size = playerList.Size;
            if (size.X > 0f && size.Y > 0f)
            {
                left = position.X + size.X + PanelGap;
                top = position.Y;
            }
        }

        panel.AnchorLeft = 0f;
        panel.AnchorTop = 0f;
        panel.AnchorRight = 0f;
        panel.AnchorBottom = 0f;
        panel.OffsetLeft = left;
        panel.OffsetTop = top;
        panel.OffsetRight = left + PanelWidth;
        panel.OffsetBottom = top + PanelHeight;
        panel.MouseFilter = Control.MouseFilterEnum.Ignore;
    }

    private static void RefreshUi(AiNodes nodes, StartRunLobby lobby)
    {
        var manager = AiTeammateManager.Instance;
        bool hasAi = manager.IsAiInLobby(lobby);

        nodes.AddBtn.Visible = !hasAi;
        nodes.CardRow.Visible = hasAi;
        nodes.HintLabel.Visible = hasAi && nodes.IsEditingAi;

        if (!hasAi)
            return;

        var aiPlayer = manager.GetAiLobbyPlayer(lobby);
        if (!aiPlayer.HasValue)
            return;

        nodes.Icon.Texture = aiPlayer.Value.character.IconTexture;
        var characterName = aiPlayer.Value.character.Title.GetFormattedText();
        nodes.NameLabel.Text = AiNames.HasCustomNickname
            ? AiLoc.T("charselect.name_custom", "AI: {Nickname} ({Character})",
                ("Nickname", AiNames.DisplayName), ("Character", characterName))
            : AiLoc.T("charselect.name", "AI: {Name}", ("Name", characterName));
        nodes.StatusLabel.Text = aiPlayer.Value.isReady
            ? AiLoc.T("charselect.status.ready", "Status: Ready")
            : AiLoc.T("charselect.status.not_ready", "Status: Not ready");
        nodes.CardBtn.TooltipText = nodes.IsEditingAi
            ? AiLoc.T("charselect.tooltip.editing", "Editing the AI teammate")
            : AiLoc.T("charselect.tooltip.edit", "Click to edit the AI teammate's character");
    }

    private static void OnAddAiClicked(NCharacterSelectScreen screen)
    {
        var aiPlayer = AiTeammateManager.Instance.AddAiToLobby(screen.Lobby);
        if (!aiPlayer.HasValue)
            return;

        screen.PlayerConnected(aiPlayer.Value);
        if (_screenNodes.TryGetValue(screen.GetInstanceId(), out var nodes))
            RefreshUi(nodes, screen.Lobby);
    }

    private static void OnRemoveAiClicked(NCharacterSelectScreen screen)
    {
        var manager = AiTeammateManager.Instance;
        var aiPlayer = manager.GetAiLobbyPlayer(screen.Lobby);
        if (!aiPlayer.HasValue)
            return;

        TryExitAiEditMode(screen);
        manager.RemoveAiFromLobby(screen.Lobby);
        screen.RemotePlayerDisconnected(aiPlayer.Value);

        if (_screenNodes.TryGetValue(screen.GetInstanceId(), out var nodes))
            RefreshUi(nodes, screen.Lobby);
    }

    private static void EnterAiEditMode(NCharacterSelectScreen screen)
    {
        var manager = AiTeammateManager.Instance;
        var aiPlayer = manager.GetAiLobbyPlayer(screen.Lobby);
        if (!aiPlayer.HasValue)
            return;

        if (aiPlayer.Value.isReady)
        {
            manager.SetAiReady(screen.Lobby, ready: false);
            aiPlayer = manager.GetAiLobbyPlayer(screen.Lobby);
            if (!aiPlayer.HasValue)
                return;
            screen.PlayerChanged(aiPlayer.Value, isRandomCharacterResolution: false);
        }

        if (_screenNodes.TryGetValue(screen.GetInstanceId(), out var nodes))
        {
            nodes.IsEditingAi = true;
            RefreshUi(nodes, screen.Lobby);
        }

        ShowLobbyPlayerSelection(screen, aiPlayer.Value, isAiEdit: true);
    }

    internal static bool HandleAiCharacterSelected(NCharacterSelectScreen screen, NCharacterSelectButton charSelectButton, CharacterModel characterModel)
    {
        var manager = AiTeammateManager.Instance;
        if (!manager.IsAiInLobby(screen.Lobby))
            return false;

        manager.SetAiCharacter(screen.Lobby, characterModel);
        var aiPlayer = manager.GetAiLobbyPlayer(screen.Lobby);
        if (!aiPlayer.HasValue)
            return false;

        ApplyCharacterPreview(screen, charSelectButton, characterModel, isAiEdit: true);
        ApplyReadyUi(screen, aiPlayer.Value, isAiEdit: true);
        screen.PlayerChanged(aiPlayer.Value, isRandomCharacterResolution: false);

        if (_screenNodes.TryGetValue(screen.GetInstanceId(), out var nodes))
            RefreshUi(nodes, screen.Lobby);

        return true;
    }

    internal static bool HandleAiEmbark(NCharacterSelectScreen screen)
    {
        var manager = AiTeammateManager.Instance;
        var aiPlayer = manager.GetAiLobbyPlayer(screen.Lobby);
        if (!aiPlayer.HasValue)
            return false;

        manager.SetAiReady(screen.Lobby, ready: true);
        aiPlayer = manager.GetAiLobbyPlayer(screen.Lobby);
        if (!aiPlayer.HasValue)
            return false;

        ApplyReadyUi(screen, aiPlayer.Value, isAiEdit: true);
        screen.PlayerChanged(aiPlayer.Value, isRandomCharacterResolution: false);

        if (_screenNodes.TryGetValue(screen.GetInstanceId(), out var nodes))
            RefreshUi(nodes, screen.Lobby);

        return true;
    }

    internal static bool HandleAiUnready(NCharacterSelectScreen screen)
    {
        var manager = AiTeammateManager.Instance;
        var aiPlayer = manager.GetAiLobbyPlayer(screen.Lobby);
        if (!aiPlayer.HasValue)
            return false;

        manager.SetAiReady(screen.Lobby, ready: false);
        aiPlayer = manager.GetAiLobbyPlayer(screen.Lobby);
        if (!aiPlayer.HasValue)
            return false;

        ApplyReadyUi(screen, aiPlayer.Value, isAiEdit: true);
        screen.PlayerChanged(aiPlayer.Value, isRandomCharacterResolution: false);

        if (_screenNodes.TryGetValue(screen.GetInstanceId(), out var nodes))
            RefreshUi(nodes, screen.Lobby);

        return true;
    }

    private static void ShowLobbyPlayerSelection(NCharacterSelectScreen screen, LobbyPlayer player, bool isAiEdit)
    {
        var targetButton = FindCharacterButton(screen, player.character);
        if (targetButton != null)
            ApplyCharacterPreview(screen, targetButton, player.character, isAiEdit);
        else
            ApplyReadyUi(screen, player, isAiEdit);
    }

    private static NCharacterSelectButton? FindCharacterButton(NCharacterSelectScreen screen, CharacterModel character)
    {
        return GetCharacterButtons(screen).FirstOrDefault(btn => btn.Character == character);
    }

    private static IEnumerable<NCharacterSelectButton> GetCharacterButtons(NCharacterSelectScreen screen)
    {
        return screen.GetNode<Control>("CharSelectButtons/ButtonContainer").GetChildren().OfType<NCharacterSelectButton>();
    }

    private static void ApplyCharacterPreview(NCharacterSelectScreen screen, NCharacterSelectButton selectedButton, CharacterModel characterModel, bool isAiEdit)
    {
        if (!selectedButton.IsRandom)
            SfxCmd.Play(characterModel.CharacterSelectSfx);

        NGame.Instance?.ScreenShake(ShakeStrength.Weak, ShakeDuration.Short, 90f);

        var infoPanel = screen.GetNode<Control>("InfoPanel");
        var bgContainer = screen.GetNode<Control>("AnimatedBg");
        var nameLabel = screen.GetNode<MegaLabel>("InfoPanel/VBoxContainer/Name");
        var descriptionLabel = screen.GetNode<MegaRichTextLabel>("InfoPanel/VBoxContainer/DescriptionLabel");
        var hpLabel = screen.GetNode<MegaLabel>("InfoPanel/VBoxContainer/HpGoldSpacer/HpGold/Hp/Label");
        var goldLabel = screen.GetNode<MegaLabel>("InfoPanel/VBoxContainer/HpGoldSpacer/HpGold/Gold/Label");
        var relicTitle = screen.GetNode<MegaRichTextLabel>("InfoPanel/VBoxContainer/Relic/Name/RichTextLabel");
        var relicDescription = screen.GetNode<MegaRichTextLabel>("InfoPanel/VBoxContainer/Relic/Description");
        var relicIcon = screen.GetNode<TextureRect>("InfoPanel/VBoxContainer/Relic/Icon");
        var relicIconOutline = screen.GetNode<TextureRect>("InfoPanel/VBoxContainer/Relic/Icon/Outline");
        var embarkButton = screen.GetNode<NConfirmButton>("ConfirmButton");
        var ascensionPanel = screen.GetNode<Control>("%AscensionPanel");

        if (InfoPanelTweenField?.GetValue(screen) is Tween tween && InfoPanelPosFinalValField?.GetValue(screen) is Vector2 finalPos)
            infoPanel.Position = finalPos;

        InfoPanelPosFinalValField?.SetValue(screen, infoPanel.Position);
        (InfoPanelTweenField?.GetValue(screen) as Tween)?.Kill();
        var newTween = screen.CreateTween().SetParallel();
        newTween.TweenProperty(infoPanel, "position", infoPanel.Position, 0.5f)
            .SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Expo)
            .From(infoPanel.Position - new Vector2(300f, 0f));
        InfoPanelTweenField?.SetValue(screen, newTween);

        foreach (Node child in bgContainer.GetChildren())
        {
            bgContainer.RemoveChildSafely(child);
            child.QueueFreeSafely();
        }

        SelectedButtonField?.SetValue(screen, selectedButton);
        MarkButtonSelection(screen, selectedButton);

        if (!selectedButton.IsLocked)
        {
            embarkButton.Enable();

            Control bg = PreloadManager.Cache.GetScene(characterModel.CharacterSelectBg)
                .Instantiate<Control>(PackedScene.GenEditState.Disabled);
            bg.Name = characterModel.Id.Entry + "_bg";
            bgContainer.AddChildSafely(bg);

            nameLabel.SetTextAutoSize(new LocString("characters", characterModel.CharacterSelectTitle).GetFormattedText());
            descriptionLabel.Text = new LocString("characters", characterModel.CharacterSelectDesc).GetFormattedText();

            if (!selectedButton.IsRandom)
            {
                hpLabel.SetTextAutoSize($"{characterModel.StartingHp}/{characterModel.StartingHp}");
                goldLabel.SetTextAutoSize($"{characterModel.StartingGold}");
                RelicModel relicModel = characterModel.StartingRelics[0];
                relicTitle.Text = relicModel.Title.GetFormattedText();
                relicDescription.Text = relicModel.DynamicDescription.GetFormattedText();
                relicIcon.Texture = relicModel.Icon;
                relicIconOutline.Texture = relicModel.IconOutline;
                relicIcon.SelfModulate = Colors.White;
                relicIconOutline.SelfModulate = StsColors.halfTransparentBlack;
            }
            else
            {
                hpLabel.SetTextAutoSize("??/??");
                goldLabel.SetTextAutoSize("???");
                relicIcon.SelfModulate = StsColors.transparentBlack;
                relicIconOutline.SelfModulate = StsColors.transparentBlack;
                relicTitle.Text = string.Empty;
                relicDescription.Text = string.Empty;
            }
        }
        else
        {
            embarkButton.Disable();
            nameLabel.SetTextAutoSize(new LocString("main_menu_ui", "CHARACTER_SELECT.locked.title").GetFormattedText());
            descriptionLabel.Text = characterModel.GetUnlockText().GetFormattedText();
            hpLabel.SetTextAutoSize("??/??");
            goldLabel.SetTextAutoSize("???");

            if (!selectedButton.IsRandom)
            {
                RelicModel relicModel = characterModel.StartingRelics[0];
                relicTitle.Text = new LocString("main_menu_ui", "CHARACTER_SELECT.lockedRelic.title").GetFormattedText();
                relicDescription.Text = new LocString("main_menu_ui", "CHARACTER_SELECT.lockedRelic.description").GetFormattedText();
                relicIcon.Texture = relicModel.Icon;
                relicIconOutline.Texture = relicModel.IconOutline;
                relicIcon.SelfModulate = StsColors.ninetyPercentBlack;
                relicIconOutline.SelfModulate = StsColors.halfTransparentWhite;
            }
            else
            {
                relicIcon.SelfModulate = StsColors.transparentBlack;
                relicIconOutline.SelfModulate = StsColors.transparentBlack;
                relicTitle.Text = string.Empty;
                relicDescription.Text = string.Empty;
            }
        }

        ascensionPanel.Visible = !isAiEdit && !selectedButton.IsLocked;
        ApplyReadyUi(screen, isAiEdit ? AiTeammateManager.Instance.GetAiLobbyPlayer(screen.Lobby)!.Value : screen.Lobby.LocalPlayer, isAiEdit);
    }

    private static void ApplyReadyUi(NCharacterSelectScreen screen, LobbyPlayer player, bool isAiEdit)
    {
        var waitingPanel = screen.GetNode<Control>("ReadyAndWaitingPanel");
        var backButton = screen.GetNode<NBackButton>("BackButton");
        var unreadyButton = screen.GetNode<NBackButton>("UnreadyButton");
        var embarkButton = screen.GetNode<NConfirmButton>("ConfirmButton");
        var ascensionPanel = screen.GetNode<Control>("%AscensionPanel");
        bool hasSelection = SelectedButtonField?.GetValue(screen) is NCharacterSelectButton btn && !btn.IsLocked;

        if (player.isReady)
        {
            SetCharacterButtonsEnabled(screen, enabled: false);
            embarkButton.Disable();
            if (isAiEdit)
                unreadyButton.Disable();
            else
                unreadyButton.Enable();
            waitingPanel.Visible = !isAiEdit && screen.Lobby.NetService.Type.IsMultiplayer() && !screen.Lobby.IsAboutToBeginGame();
            if (isAiEdit)
                backButton.Enable();
            else
                backButton.Disable();
        }
        else
        {
            SetCharacterButtonsEnabled(screen, enabled: true);
            waitingPanel.Visible = false;
            backButton.Enable();
            unreadyButton.Disable();
            if (hasSelection)
                embarkButton.Enable();
            else
                embarkButton.Disable();
        }

        if (isAiEdit)
            ascensionPanel.Visible = false;
    }

    private static void SetCharacterButtonsEnabled(NCharacterSelectScreen screen, bool enabled)
    {
        foreach (var button in GetCharacterButtons(screen))
        {
            if (enabled)
                button.Enable();
            else
                button.Disable();
        }
    }

    private static void MarkButtonSelection(NCharacterSelectScreen screen, NCharacterSelectButton target)
    {
        foreach (var button in GetCharacterButtons(screen))
        {
            IsSelectedField?.SetValue(button, ReferenceEquals(button, target));
            RefreshStateMethod?.Invoke(button, null);
        }
    }
}

[HarmonyLib.HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.SelectCharacter))]
public static class CharacterSelectScreenSelectCharacterPatch
{
    [HarmonyLib.HarmonyPrefix]
    public static bool Prefix(NCharacterSelectScreen __instance, NCharacterSelectButton charSelectButton, CharacterModel characterModel)
    {
        if (!CharacterSelectScreenPatch.IsEditingAi(__instance))
            return true;

        CharacterSelectScreenPatch.HandleAiCharacterSelected(__instance, charSelectButton, characterModel);
        return false;
    }
}

[HarmonyLib.HarmonyPatch(typeof(NCharacterSelectScreen), "OnEmbarkPressed")]
public static class CharacterSelectScreenEmbarkPatch
{
    [HarmonyLib.HarmonyPrefix]
    public static bool Prefix(NCharacterSelectScreen __instance, NButton _)
    {
        if (!CharacterSelectScreenPatch.IsEditingAi(__instance))
            return true;

        CharacterSelectScreenPatch.HandleAiEmbark(__instance);
        return false;
    }
}

[HarmonyLib.HarmonyPatch(typeof(NCharacterSelectScreen), "OnUnreadyPressed")]
public static class CharacterSelectScreenUnreadyPatch
{
    [HarmonyLib.HarmonyPrefix]
    public static bool Prefix(NCharacterSelectScreen __instance, NButton _)
    {
        if (!CharacterSelectScreenPatch.IsEditingAi(__instance))
            return true;

        CharacterSelectScreenPatch.HandleAiUnready(__instance);
        return false;
    }
}

[HarmonyLib.HarmonyPatch(typeof(NSubmenuStack), nameof(NSubmenuStack.Pop))]
public static class CharacterSelectScreenBackPatch
{
    [HarmonyLib.HarmonyPrefix]
    public static bool Prefix(NSubmenuStack __instance)
    {
        if (__instance.Peek() is NCharacterSelectScreen screen && CharacterSelectScreenPatch.TryExitAiEditMode(screen))
            return false;

        return true;
    }
}
