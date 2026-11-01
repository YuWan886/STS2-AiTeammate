using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Daily;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Random;

namespace AiTeammate.AiTeammateCode.Compat;

/// <summary>
/// The daily run's fixed setup (seed / character / ascension / 2 good + 1 bad modifiers) is rolled
/// by <c>NDailyRunScreen.SetupLobbyParams</c> at the moment the daily lobby opens. When dsh starts
/// today's daily directly from the main menu we have to roll the very same values ourselves, with
/// the same RNG chain, or the run stops being "the daily".
/// 外部 Agent 直接开每日时，按游戏同样的 RNG 链自己算出每日的种子/角色/进阶/词条。
/// </summary>
internal static class DailyRunCompat
{
    /// <summary>All values a daily run needs, plus the server time to store in the run save.</summary>
    internal sealed class DailyPlan
    {
        public required string Seed { get; init; }
        public required int Ascension { get; init; }
        public required CharacterModel Character { get; init; }
        public required List<ModifierModel> Modifiers { get; init; }
        public required DateTimeOffset ServerTime { get; init; }
    }

    /// <summary>
    /// Rolls the daily setup for <paramref name="playerCount"/> players. Mirrors
    /// NDailyRunScreen.SetupLobbyParams: the date-only string seeds the RNG chain, the run seed
    /// carries the player count ("dd_MM_yyyy_{n}p"), rng2 picks characters in lobby order, rng3 the
    /// ascension and rng4 the modifiers. 与游戏每日大厅一致的随机链。
    /// </summary>
    public static DailyPlan Compute(TimeServerResult time, int playerCount)
    {
        var count = Math.Max(1, playerCount);
        var serverRelativeTime = time.serverTime + (DateTimeOffset.UtcNow - time.localReceivedTime);

        var dateSeed = SeedHelper.CanonicalizeSeed(serverRelativeTime.ToString("dd_MM_yyyy"));
        var runSeed = SeedHelper.CanonicalizeSeed(serverRelativeTime.ToString($"dd_MM_yyyy_{count}p"));

#if STS2_API_0_111_OR_LATER
        var rng = new Rng(StringHelper.GetDeterministicHashCode(dateSeed));
        var rng2 = new Rng(rng.NextUnsignedLong());
        var rng3 = new Rng(rng.NextUnsignedLong());
        var rng4 = new Rng(rng.NextUnsignedLong());
#else
        var rng = new Rng((uint)StringHelper.GetDeterministicHashCode(dateSeed));
        var rng2 = new Rng(rng.NextUnsignedInt());
        var rng3 = new Rng(rng.NextUnsignedInt());
        var rng4 = new Rng(rng.NextUnsignedInt());
#endif

        var character = rng2.NextItem(ModelDb.AllCharacters)
                        ?? throw new InvalidOperationException("There are no characters available for a daily run.");
        var ascension = rng3.NextInt(0, 11);
        var modifiers = RollModifiers(rng4, new[] { character });

        return new DailyPlan
        {
            Seed = runSeed,
            Ascension = ascension,
            Character = character,
            Modifiers = modifiers,
            ServerTime = time.serverTime
        };
    }

    /// <summary>
    /// dsh starts a singleplayer daily, so the player list is just the rolled character; the
    /// CharacterCards modifier must not hand out a character the player already plays.
    /// </summary>
    private static List<ModifierModel> RollModifiers(Rng rng, IReadOnlyList<CharacterModel> players)
    {
#if STS2_API_0_111_OR_LATER
        return ModifierModel.Pick2Good1Bad(rng, players).ToList();
#else
        // 0.107 keeps the roll inside NDailyRunScreen; reproduce it here.
        var result = new List<ModifierModel>();
        var goodPool = ModelDb.GoodModifiers.ToList().StableShuffle(rng);
        for (var i = 0; i < 2; i++)
        {
            var canonical = rng.NextItem(goodPool);
            if (canonical == null)
                throw new InvalidOperationException("There were not enough good modifiers to fill the daily!");

            var mutable = canonical.ToMutable();
            if (mutable is CharacterCards characterCards)
            {
                var candidates = ModelDb.AllCharacters.Except(players).ToList();
                if (candidates.Count == 0)
                    candidates = ModelDb.AllCharacters.ToList();
                characterCards.CharacterModel = rng.NextItem(candidates)!.Id;
            }

            result.Add(mutable);
            goodPool.Remove(canonical);

            var mutuallyExclusive = ModelDb.MutuallyExclusiveModifiers
                .FirstOrDefault(set => set.Contains(canonical));
            if (mutuallyExclusive == null)
                continue;

            foreach (var excluded in mutuallyExclusive)
                goodPool.Remove(excluded);
        }

        var badModifier = rng.NextItem(ModelDb.BadModifiers)
                          ?? throw new InvalidOperationException("There were no bad modifiers to fill the daily!");
        result.Add(badModifier.ToMutable());
        return result;
#endif
    }
}
