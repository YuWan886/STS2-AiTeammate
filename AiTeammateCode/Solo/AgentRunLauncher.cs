using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Daily;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Localization;
using AiTeammate.AiTeammateCode.Settings;

namespace AiTeammate.AiTeammateCode.Solo;

/// <summary>
/// The run kinds the external agent can start: a normal run, today's daily (server time + the
/// daily seed/modifiers) or a custom run with explicit modifiers.
/// 外部 Agent 可开的三种单人局。
/// </summary>
internal enum AgentRunMode
{
    Standard,
    Daily,
    Custom
}

/// <summary>
/// Starts singleplayer runs for the external agent. Unlike the mod's own solo climb this never
/// turns <c>EnableSoloClimb</c> on: dsh drives the player itself.
/// 给 dsh 开局（标准 / 每日 / 自定），不开启内置单人爬塔。
/// </summary>
internal static class AgentRunLauncher
{
    public static AgentRunMode ParseMode(string? text)
    {
        return text?.Trim().ToLowerInvariant() switch
        {
            "daily" => AgentRunMode.Daily,
            "custom" => AgentRunMode.Custom,
            _ => AgentRunMode.Standard
        };
    }

    /// <summary>Starts a run and returns immediately; the actual scene load happens asynchronously.</summary>
    public static (bool Ok, string Message) Start(string? modeText, string? characterId, int ascensionLevel,
        string? seed, IReadOnlyList<string>? modifierIds)
    {
        var mode = ParseMode(modeText);
        if (mode == AgentRunMode.Daily)
            return StartDaily();

        return StartLocal(mode, characterId, ascensionLevel, seed, modifierIds);
    }

    // ---- Standard / custom -------------------------------------------------------------

    private static (bool Ok, string Message) StartLocal(AgentRunMode mode, string? characterId,
        int ascensionLevel, string? seed, IReadOnlyList<string>? modifierIds)
    {
        var game = NGame.Instance;
        if (game == null)
            return (false, "The game isn't ready yet.");
        if (!SoloRunController.CanStartNewRun)
            return (false, "A run is already in progress; finish or abandon it first.");

        try
        {
            var manager = AiTeammateManager.Instance;
            if (!manager.IsInitialized)
                manager.Initialize();

            var character = ResolveCharacter(manager, characterId);
            if (character == null)
                return (false, "No available character was found.");

            if (!TryResolveModifiers(modifierIds, character, out var modifiers, out var modifierError))
                return (false, modifierError);

            var ascension = Math.Clamp(ascensionLevel, 0, SoloRunController.MaxAscensionLevel);
            seed = string.IsNullOrWhiteSpace(seed) ? Guid.NewGuid().ToString("N")[..10] : seed.Trim();

            if (!TryBuildActs(seed, out var acts))
                return (false, "No acts are available.");

            // dsh plays the local player itself; the built-in solo driver must not compete.
            DisableSoloClimb();

            var isCustom = mode == AgentRunMode.Custom;
            var gameMode = isCustom ? GameCompat.CustomRunGameMode : GameMode.Standard;
            var characterName = PromptTextHelper.ResolveLocString(character.Title, character.Id.Entry);
            var modifierText = modifiers.Count == 0
                ? "none"
                : string.Join(", ", modifiers.Select(m => m.Id.Entry));
            MainFile.Logger.Info($"[Agent] starting {gameMode} run for dsh: char={character.Id.Entry} " +
                                 $"seed={seed} acts={acts.Count} ascension={ascension} modifiers={modifierText}");

            _ = RunAndLogAsync(game, character, seed, acts, ascension, modifiers, gameMode, isCustom, dailyTime: null);
            return (true, $"Starting a {gameMode.ToString().ToLowerInvariant()} run with {characterName} "
                          + $"(ascension {ascension}, seed {seed}, modifiers: {modifierText}).");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Agent] start run failed: {ex}");
            return (false, "Failed to start: " + ex.Message);
        }
    }

    // ---- Daily -------------------------------------------------------------------------

    private static (bool Ok, string Message) StartDaily()
    {
        var game = NGame.Instance;
        if (game == null)
            return (false, "The game isn't ready yet.");
        if (!SoloRunController.CanStartNewRun)
            return (false, "A run is already in progress; finish or abandon it first.");

        // The daily needs the MegaCrit time server; fetch it off the main thread and start the run
        // when it lands (the game's own daily screen does the same).
        _ = StartDailyAsync(game);
        return (true, "Fetching today's daily from the time server, then starting the run. "
                      + "Call sts2_wait until the run appears.");
    }

    private static async Task StartDailyAsync(NGame game)
    {
        TimeServerResult time;
        try
        {
            var fetched = await TimeServer.FetchDailyTime();
            if (fetched is not null)
            {
                time = fetched.Value;
            }
            else
            {
                MainFile.Logger.Warn("[Agent] daily time server returned nothing; using local time");
                time = LocalDailyTime();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            MainFile.Logger.Warn($"[Agent] daily time server unreachable ({ex.GetType().Name}); using local time");
            time = LocalDailyTime();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Agent] daily time fetch failed: {ex}");
            return;
        }

        try
        {
            MainThread.Run(() => StartDailyNow(game, time));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Agent] daily start failed: {ex}");
        }
    }

    private static void StartDailyNow(NGame game, TimeServerResult time)
    {
        if (!SoloRunController.CanStartNewRun)
        {
            MainFile.Logger.Warn("[Agent] a run started before the daily time arrived; daily cancelled");
            return;
        }

        var plan = DailyRunCompat.Compute(time, playerCount: 1);
        if (!TryBuildActs(plan.Seed, out var acts))
        {
            MainFile.Logger.Error("[Agent] no acts are available for the daily run");
            return;
        }

        DisableSoloClimb();

        var characterName = PromptTextHelper.ResolveLocString(plan.Character.Title, plan.Character.Id.Entry);
        var modifierText = string.Join(", ", plan.Modifiers.Select(m => m.Id.Entry));
        MainFile.Logger.Info($"[Agent] starting DAILY run for dsh: char={plan.Character.Id.Entry} " +
                             $"seed={plan.Seed} ascension={plan.Ascension} modifiers={modifierText} " +
                             $"serverTime={plan.ServerTime:O}");

        _ = RunAndLogAsync(game, plan.Character, plan.Seed, acts, plan.Ascension, plan.Modifiers,
            GameMode.Daily, isCustom: false, plan.ServerTime);

        try
        {
            AiTeammateManager.Instance.ChatManager.AddSystemMessage(
                $"今日每日已开始：{characterName}，种子 {plan.Seed}，进阶 {plan.Ascension}。");
        }
        catch
        {
            // Chat is cosmetic here.
        }
    }

    private static TimeServerResult LocalDailyTime()
    {
        var now = DateTimeOffset.UtcNow;
        return new TimeServerResult { serverTime = now, localReceivedTime = now };
    }

    // ---- Shared helpers ----------------------------------------------------------------

    private static CharacterModel? ResolveCharacter(AiTeammateManager manager, string? characterId)
    {
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
        return character;
    }

    private static bool TryBuildActs(string seed, out List<ActModel> acts)
    {
        var unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();
        var rng = new Rng((uint)StringHelper.GetDeterministicHashCode(seed), "act_selection");
        acts = ActModel.GetRandomList(rng, unlockState, isMultiplayer: false).ToList();
        return acts.Count > 0;
    }

    /// <summary>Resolves dsh's modifier ids/titles into mutable <see cref="ModifierModel"/> instances.</summary>
    private static bool TryResolveModifiers(IReadOnlyList<string>? ids, CharacterModel character,
        out List<ModifierModel> modifiers, out string error)
    {
        modifiers = new List<ModifierModel>();
        error = "";
        if (ids == null || ids.Count == 0)
            return true;

        var pool = ModelDb.GoodModifiers.Concat(ModelDb.BadModifiers).ToList();
        var canonical = new List<ModifierModel>();

        foreach (var raw in ids)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var text = raw.Trim();
            string? requestedCharacter = null;
            var colon = text.IndexOf(':');
            if (colon > 0)
            {
                requestedCharacter = text[(colon + 1)..].Trim();
                text = text[..colon].Trim();
            }

            var found = pool.FirstOrDefault(m => string.Equals(m.Id.Entry, text, StringComparison.OrdinalIgnoreCase))
                        ?? pool.FirstOrDefault(m => string.Equals(ResolveTitle(m), text, StringComparison.OrdinalIgnoreCase))
                        ?? pool.FirstOrDefault(m => m.Id.Entry.Contains(text, StringComparison.OrdinalIgnoreCase));
            if (found == null)
            {
                error = $"Unknown modifier '{raw}'. Known modifiers: "
                        + string.Join(", ", pool.Select(m => m.Id.Entry));
                return false;
            }

            if (canonical.Contains(found))
                continue;

            var mutable = found.ToMutable();
            if (mutable is CharacterCards characterCards)
            {
                var pick = ResolveCharacterModel(requestedCharacter)
                           ?? ModelDb.AllCharacters.FirstOrDefault(c => c.Id.Entry != character.Id.Entry)
                           ?? character;
                characterCards.CharacterModel = pick.Id;
            }

            canonical.Add(found);
            modifiers.Add(mutable);
        }

        foreach (var group in ModelDb.MutuallyExclusiveModifiers)
        {
            var hits = canonical.Where(group.Contains).ToList();
            if (hits.Count <= 1)
                continue;

            error = "Those modifiers are mutually exclusive: "
                    + string.Join(", ", hits.Select(m => m.Id.Entry));
            return false;
        }

        return true;
    }

    private static CharacterModel? ResolveCharacterModel(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return ModelDb.AllCharacters.FirstOrDefault(c =>
                   string.Equals(c.Id.Entry, id, StringComparison.OrdinalIgnoreCase))
               ?? ModelDb.AllCharacters.FirstOrDefault(c =>
                   c.Id.Entry.Contains(id, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveTitle(ModifierModel modifier)
    {
        try
        {
            return PromptTextHelper.ResolveLocString(modifier.Title, modifier.Id.Entry);
        }
        catch
        {
            return modifier.Id.Entry;
        }
    }

    private static void DisableSoloClimb()
    {
        if (!AiSettingsStore.Current.EnableSoloClimb)
            return;
        AiSettingsStore.Current.EnableSoloClimb = false;
        AiSettingsStore.Save();
    }

    private static async Task RunAndLogAsync(NGame game, CharacterModel character,
        string seed, List<ActModel> acts, int ascensionLevel, IReadOnlyList<ModifierModel> modifiers,
        GameMode gameMode, bool isCustom, DateTimeOffset? dailyTime)
    {
        try
        {
            await GameCompat.StartNewSingleplayerRunAsync(game, character, acts, modifiers, seed, gameMode, isCustom,
                ascensionLevel, dailyTime);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[Agent] StartNewSingleplayerRun ({gameMode}) failed: {ex}");
            try
            {
                AiTeammateManager.Instance.ChatManager.AddSystemMessage(
                    AiLoc.T("system.solo_start_failed", "Failed to start the AI climb: {Message}",
                        ("Message", ex.Message)));
            }
            catch
            {
                // Chat may not exist yet.
            }
        }
    }
}
