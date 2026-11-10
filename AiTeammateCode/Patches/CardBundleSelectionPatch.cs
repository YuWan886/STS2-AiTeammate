using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using AiTeammate.AiTeammateCode.Agent;
using AiTeammate.AiTeammateCode.Compat;
using AiTeammate.AiTeammateCode.Decision;
using AiTeammate.AiTeammateCode.Personality;
using AiTeammate.AiTeammateCode.Solo;

namespace AiTeammate.AiTeammateCode.Patches;

/// <summary>
/// 「卷轴箱」这类「从两包牌里挑一包」的界面（NChooseABundleSelectionScreen）原本没有任何 AI 钩子：
/// 屏幕会一直挂在 overlay 栈上，AI 侧等待的房间流程永远不返回，整个 run 软锁（桥接报 ai_busy、不再出决策）。
/// 这里补上：认出这是 AI 玩家的卡包 → 挂一个 bundle_select 决策交给 dsh（没接 dsh 就走启发式）→
/// 把选中的包写回屏幕的完成源。加牌动画、关闭、选择同步都还由游戏自己走。
/// </summary>
[HarmonyPatch(typeof(NChooseABundleSelectionScreen), nameof(NChooseABundleSelectionScreen.AfterOverlayShown))]
internal static class CardBundleSelectionPatch
{
    private static readonly FieldInfo? BundlesField =
        AccessTools.Field(typeof(NChooseABundleSelectionScreen), "_bundles");

    private static readonly FieldInfo? CompletionSourceField =
        AccessTools.Field(typeof(NChooseABundleSelectionScreen), "_completionSource");

    private static int _handling;

    [HarmonyPostfix]
    public static void AfterOverlayShown(NChooseABundleSelectionScreen __instance)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(__instance)) return;

            // 单人爬塔：SoloRunController 已经给这个界面注册了 AutoSlay 处理器，别抢。
            if (SoloRunController.IsActive) return;

            var manager = AiTeammateManager.Instance;
            if (manager == null || !manager.IsInitialized || manager.AiPlayer == null) return;

            var bundles = BundlesField?.GetValue(__instance) as IReadOnlyList<IReadOnlyList<CardModel>>;
            if (bundles == null || bundles.Count == 0) return;

            var ownership = ClassifyOwnership(bundles, manager.AiPlayer);
            if (ownership == BundleOwnership.Local) return;      // 真人的卡包，交给真人
            if (ownership == BundleOwnership.Ambiguous)
            {
                MainFile.Logger.Warn("[AI] bundle screen has no card owners; leaving it to the player");
                return;
            }

            if (Interlocked.CompareExchange(ref _handling, 1, 0) != 0) return;
            _ = HandleAsync(__instance, manager, manager.AiPlayer, bundles);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[AI] bundle selection patch failed: {ex.Message}");
        }
    }

    private enum BundleOwnership
    {
        Ai,
        Local,
        Ambiguous
    }

    /// <summary>
    /// A bundle's cards are created with <c>RunState.CreateCard(card, owner)</c>, so the cards carry the
    /// choosing player as Owner — that is the only reliable way to tell the AI's pack screen apart from
    /// the human's, since both land on the same shared overlay stack.
    /// </summary>
    private static BundleOwnership ClassifyOwnership(
        IReadOnlyList<IReadOnlyList<CardModel>> bundles, Player aiPlayer)
    {
        ulong? localNetId = null;
        try { localNetId = LocalContext.NetId; } catch { }

        var sawOwner = false;
        var sawAi = false;
        foreach (var bundle in bundles)
        {
            foreach (var card in bundle)
            {
                ulong? owner = card?.Owner?.NetId;
                if (owner == null) continue;

                sawOwner = true;
                if (owner.Value == aiPlayer.NetId)
                {
                    sawAi = true;
                    continue;
                }

                if (localNetId.HasValue && owner.Value == localNetId.Value)
                    return BundleOwnership.Local;
            }
        }

        if (!sawOwner) return BundleOwnership.Ambiguous;
        return sawAi ? BundleOwnership.Ai : BundleOwnership.Ambiguous;
    }

    private static async Task HandleAsync(
        NChooseABundleSelectionScreen screen,
        AiTeammateManager manager,
        Player aiPlayer,
        IReadOnlyList<IReadOnlyList<CardModel>> bundles)
    {
        try
        {
            // Everything below runs on the main thread until the first await, so touching the screen
            // and reading game models here is safe.
            var (prompt, options) = BuildPrompt(aiPlayer, bundles);

            // The AI's flow is parked on this screen: hide it so the human isn't staring at a frozen
            // overlay (and can't click the AI's choice for it).
            try
            {
                screen.Visible = false;
                NOverlayStack.Instance?.HideBackstop();
            }
            catch { }

            var answer = await AskDshAsync(aiPlayer, prompt, options);

            var choice = ParseChoice(answer, bundles.Count);
            var source = "dsh";
            if (choice < 0)
            {
                choice = PickByHeuristic(bundles);
                source = "heuristic";
            }

            var chosen = bundles[choice];
            var names = string.Join(" + ", chosen.Select(DescribeCardShort));
            MainFile.Logger.Info($"AI bundle choice {choice} ({source}): {names}");
            manager.AiLog.Log("AI_THINK", $"[选包] 选择第 {choice} 包：{names}");
            manager.AiLog.Log("AI_ACTION", $"卡包选择: 第 {choice} 包 [{names}] | {source}");

            MainThread.Post(() => Complete(screen, chosen));
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[AI] bundle selection failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _handling, 0);
        }
    }

    /// <summary>
    /// Hands the selection to the parked completion source. Backfilling through the raw list keeps the
    /// reference identity intact: <c>CardSelectCmd.FromChooseABundleScreen</c> maps the returned bundle
    /// back to its index with <c>bundles.IndexOf(...)</c> before syncing the choice, so a copied list
    /// would sync "-1" (an empty pick) instead of the chosen pack.
    /// </summary>
    private static void Complete(NChooseABundleSelectionScreen screen, IReadOnlyList<CardModel> chosen)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(screen)) return;

            var tcs = CompletionSourceField?.GetValue(screen)
                as TaskCompletionSource<IEnumerable<IReadOnlyList<CardModel>>>;
            if (tcs == null)
            {
                MainFile.Logger.Warn("[AI] bundle screen completion source unavailable");
                return;
            }

            if (!tcs.Task.IsCompleted)
                tcs.TrySetResult(new List<IReadOnlyList<CardModel>> { chosen });

            // CardsSelected() removes the screen from the overlay stack once the task completes, and
            // AfterOverlayClosed frees it; no manual teardown here.
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[AI] bundle completion failed: {ex.Message}");
        }
    }

    private static async Task<string?> AskDshAsync(Player aiPlayer, string prompt, System.Text.Json.Nodes.JsonArray options)
    {
        if (!AgentDecisionBroker.Enabled) return null;

        var systemPrompt = PersonalityPromptBuilder.BuildExternalAgentSystemPrompt(
            requireJson: true, includeTeamPrinciples: false);

        // The game's room flow is blocked on this screen: cap it with the out-of-combat decision limit
        // instead of riding the broker's full budget.
        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(DecisionService.OutOfCombatTimeoutMs));
        return await AgentDecisionBroker.RequestAsync(
            aiPlayer, "bundle_select", systemPrompt, prompt, timeoutCts.Token, options);
    }

    private static (string Prompt, System.Text.Json.Nodes.JsonArray Options) BuildPrompt(
        Player aiPlayer, IReadOnlyList<IReadOnlyList<CardModel>> bundles)
    {
        var prompt = new StringBuilder();
        var options = new System.Text.Json.Nodes.JsonArray();

        prompt.AppendLine("[卡包选择]");
        prompt.AppendLine($"从下面的 {bundles.Count} 个卡包里选择 1 包，整包加入牌组（这些牌是本次事件给的，不用花金币）。");
        prompt.AppendLine(
            $"{aiPlayer.Character?.Title?.GetRawText() ?? aiPlayer.Character?.Id.Entry ?? "AI"} | " +
            $"HP:{aiPlayer.Creature?.CurrentHp ?? 0}/{aiPlayer.Creature?.MaxHp ?? 0} | " +
            $"金币:{aiPlayer.Gold} | 牌组:{aiPlayer.Deck?.Cards.Count ?? 0}张");
        prompt.AppendLine();

        for (var i = 0; i < bundles.Count; i++)
        {
            var bundle = bundles[i];
            prompt.AppendLine($"{i}: {string.Join(" + ", bundle.Select(DescribeCardShort))}");
            foreach (var card in bundle)
            {
                var desc = PromptTextHelper.ResolveCardDescription(card, PileType.None);
                if (!string.IsNullOrWhiteSpace(desc))
                    prompt.AppendLine($"   {DescribeCardShort(card)}：{desc}");
            }

            var cards = new System.Text.Json.Nodes.JsonArray();
            foreach (var card in bundle)
            {
                cards.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = card.Title,
                    ["cost"] = DescribeCost(card),
                    ["type"] = card.Type.ToString(),
                    ["description"] = PromptTextHelper.ResolveCardDescription(card, PileType.None)
                });
            }

            options.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["index"] = i,
                ["cards"] = cards
            });
        }

        prompt.AppendLine();
        prompt.AppendLine($"请以 JSON 返回：{{\"choice\":0~{bundles.Count - 1}, \"reason\":\"理由\"}}（choice 是卡包序号）");
        return (prompt.ToString(), options);
    }

    private static string DescribeCardShort(CardModel card)
    {
        var name = card.Title;
        return $"{name}({DescribeCost(card)}费/{card.Type})";
    }

    private static string DescribeCost(CardModel card)
    {
        try { return card.EnergyCost.CostsX ? "X" : PromptTextHelper.ResolveEnergyCost(card).ToString(); }
        catch { return "?"; }
    }

    private static int ParseChoice(string? answer, int bundleCount)
    {
        if (string.IsNullOrWhiteSpace(answer)) return -1;

        try
        {
            using var doc = JsonDocument.Parse(ResponseParser.ExtractJson(answer));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return -1;

            foreach (var key in new[] { "choice", "index", "bundle" })
            {
                if (!root.TryGetProperty(key, out var value)) continue;
                var index = ReadIndex(value);
                if (index >= 0 && index < bundleCount) return index;
            }
        }
        catch
        {
            // Malformed answer: the heuristic fallback takes over.
        }

        return -1;
    }

    private static int ReadIndex(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
            return number;
        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out number))
            return number;
        return -1;
    }

    /// <summary>No dsh answer (or broker off): take the bundle holding the best single card.</summary>
    private static int PickByHeuristic(IReadOnlyList<IReadOnlyList<CardModel>> bundles)
    {
        try
        {
            var flat = bundles.SelectMany(b => b).ToList();
            var best = CardGridSelectionPatch.PickCardsByHeuristic(flat, 1, 1, "BundleSelect").FirstOrDefault();
            if (best == null) return 0;

            for (var i = 0; i < bundles.Count; i++)
            {
                if (bundles[i].Any(c => ReferenceEquals(c, best) || c?.Id == best.Id))
                    return i;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[AI] bundle heuristic failed: {ex.Message}");
        }

        return 0;
    }
}
