using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using AiTeammate.AiTeammateCode.Patches;

namespace AiTeammate.AiTeammateCode.Compat;

// 0.113 removed the CharacterModel argument from NCharacterSelectScreen.SelectCharacter; the chosen character now
// lives on the clicked button. Harmony binds prefix parameters by name, so the patch method itself changes shape
// per game version — that is why this one patch class lives in Compat/ instead of Patches/.
#if STS2_API_0_113_OR_LATER
[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.SelectCharacter))]
public static class CharacterSelectScreenSelectCharacterPatch
{
    [HarmonyPrefix]
    public static bool Prefix(NCharacterSelectScreen __instance, NCharacterSelectButton charSelectButton)
    {
        if (!CharacterSelectScreenPatch.IsEditingAi(__instance))
            return true;

        CharacterSelectScreenPatch.HandleAiCharacterSelected(__instance, charSelectButton, charSelectButton.Character);
        return false;
    }
}
#else
[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.SelectCharacter))]
public static class CharacterSelectScreenSelectCharacterPatch
{
    [HarmonyPrefix]
    public static bool Prefix(NCharacterSelectScreen __instance, NCharacterSelectButton charSelectButton, CharacterModel characterModel)
    {
        if (!CharacterSelectScreenPatch.IsEditingAi(__instance))
            return true;

        CharacterSelectScreenPatch.HandleAiCharacterSelected(__instance, charSelectButton, characterModel);
        return false;
    }
}
#endif
