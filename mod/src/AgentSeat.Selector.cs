using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

// 动作层（第 1 块）：卡牌选择。参考 dsh-sts2-main：
//   用 CardSelectCmd.UseSelector 挂一个自己的 ICardSelector —— 界面不弹，游戏直接问我们要答案。
public static partial class AgentSeatEntry
{
    static IDisposable _selectorScope;
    static int _kindProbeCount;
    static long _selectorInstalledAt;

    /// <summary>每帧调用：代理模式下保证 selector 是我们的（一局结束会被 Reset 掉）。</summary>
    public static void TickSelector()
    {
        try
        {
            if (!AgentMode()) return;
            if (Environment.TickCount64 - _selectorInstalledAt < 2000) return;
            _selectorInstalledAt = Environment.TickCount64;

            // 必须等 AutoSlay 先装它自己的：UseSelector 在栈非空时会抛
            // "A card selector is already active"，我们抢先压栈会把 AutoSlay 打崩。
            if (CardSelectCmd.Selector == null) return;
            if (CardSelectCmd.Selector is AgentCardSelector) return;   // 已经在栈顶

            // 必须用 PushSelector：UseSelector 是独占语义，AutoSlay 装自己那个时会抛
            // "A card selector is already active"，直接把整局打崩。
            _selectorScope = CardSelectCmd.PushSelector(new AgentCardSelector());
            W("选牌 selector: 已压栈 → " + (CardSelectCmd.Selector is AgentCardSelector ? "栈顶是我们" : "失败"));
        }
        catch (Exception e) { W("选牌 selector 安装失败: " + e.GetType().Name + ": " + e.Message); }
    }

    /// <summary>真正的钩子：游戏把候选牌交进来，我们问模型，等答案。</summary>
    sealed class AgentCardSelector : ICardSelector
    {
        Task<IEnumerable<CardModel>> ICardSelector.GetSelectedCards(IEnumerable<CardModel> cards, int min, int max)
        {
            return AskForCards(cards, min, max);
        }

        CardRewardSelection ICardSelector.GetSelectedCardReward(
            IReadOnlyList<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult> options,
            IReadOnlyList<MegaCrit.Sts2.Core.Entities.CardRewardAlternatives.CardRewardAlternative> alternatives)
        {
            return default(CardRewardSelection);
        }
    }

    /// <summary>
    /// 非阻塞：主线程只做「收集候选 + 组 payload」（毫秒级），
    /// HTTP 问模型放到后台线程，立刻返回一个"稍后完成"的 Task —— 游戏不会卡住画面。
    /// 参考 dsh-sts2-main 的 BeginCardRequest。
    /// </summary>
    static Task<IEnumerable<CardModel>> AskForCards(IEnumerable<CardModel> cards, int min, int max)
    {
        List<CardModel> models = new List<CardModel>();
        try { if (cards != null) foreach (CardModel c in cards) if (c != null) models.Add(c); }
        catch (Exception e) { W("枚举候选牌失败: " + e.Message); }

        if (models.Count == 0) return Task.FromResult<IEnumerable<CardModel>>(new CardModel[0]);

        string kind, caller;
        DetectCardKind(out kind, out caller);
        int want = Math.Max(1, Math.Min(min <= 0 ? 1 : min, models.Count));

        string payload = null;
        try { payload = BuildSelectorPayload(models, kind, caller, min, max); }
        catch (Exception e) { W("组选牌 payload 失败: " + e.Message); }

        // 兜底答案先在主线程算好（不依赖主线程）
        List<CardModel> fallback = new List<CardModel>();
        try
        {
            foreach (int idx in FallbackPicksForModels(models, kind, want))
                if (idx >= 0 && idx < models.Count) fallback.Add(models[idx]);
        }
        catch { }

        if (string.IsNullOrEmpty(payload))
            return Task.FromResult<IEnumerable<CardModel>>(fallback);

        int framesAtStart = _frames;
        TaskCompletionSource<IEnumerable<CardModel>> tcs = new TaskCompletionSource<IEnumerable<CardModel>>();

        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                string resp = Ask(payload, "pick_cards");
                List<int> picks = ParsePicks(resp);
                List<CardModel> chosen = new List<CardModel>();
                for (int i = 0; i < picks.Count && chosen.Count < want; i++)
                {
                    int idx = picks[i];
                    if (idx >= 0 && idx < models.Count && !chosen.Contains(models[idx])) chosen.Add(models[idx]);
                }
                if (chosen.Count < want)
                {
                    W("selector: picks 不够（要 " + want + "，给 " + chosen.Count + "），按分数补");
                    for (int i = 0; i < fallback.Count && chosen.Count < want; i++)
                        if (!chosen.Contains(fallback[i])) chosen.Add(fallback[i]);
                }
                StringBuilder nm = new StringBuilder();
                for (int i = 0; i < chosen.Count; i++) { if (i > 0) nm.Append(" / "); nm.Append(chosen[i].Title); }
                W("selector[" + kind + "/" + (caller ?? "?") + "]: 选了 " + nm
                    + "（决策期间主线程推进 " + (_frames - framesAtStart) + " 帧）");
                tcs.SetResult(chosen);
            }
            catch (Exception e)
            {
                W("selector 决策失败: " + e.Message + "（用兜底）");
                try { tcs.SetResult(fallback); } catch { }
            }
        });

        return tcs.Task;   // 立刻返回，未完成
    }

    /// <summary>参考 dsh-sts2-main 的 DetectKind：栈里找 async 状态机 &lt;FromDeckForUpgrade&gt;d__N。</summary>
    static void DetectCardKind(out string kind, out string caller)
    {
        kind = "simple";
        caller = null;

        // 入口钩子记下的用途优先级最高：栈里的 FromDeckGeneric 之类名字不带方向，
        // 而 PrefixSelectPurpose 已经读了 prefs（RemoveSelectionPrompt 等）判定过。
        if (!string.IsNullOrEmpty(_pendingPurpose) && _pendingPurpose != "simple")
        {
            kind = _pendingPurpose;
            caller = "prefs";
            _pendingPurpose = "";
            return;
        }

        try
        {
            StackFrame[] frames = new StackTrace(false).GetFrames();
            if (frames != null)
            {
                for (int i = 0; i < frames.Length; i++)
                {
                    Type t = frames[i] == null || frames[i].GetMethod() == null ? null : frames[i].GetMethod().DeclaringType;
                    if (t == null) continue;
                    string tn = t.Name;
                    int s = tn.IndexOf("<From", StringComparison.Ordinal);
                    if (s < 0) continue;
                    string name = tn.Substring(s + 1);
                    int e = name.IndexOf('>');
                    if (e > 0) name = name.Substring(0, e);
                    caller = name;
                    break;
                }
            }
        }
        catch { }

        try
        {
            if (caller == null && _kindProbeCount < 3)
            {
                _kindProbeCount++;
                StringBuilder st = new StringBuilder();
                StackFrame[] fs = new StackTrace(false).GetFrames();
                if (fs != null)
                    for (int i = 0; i < fs.Length && i < 10; i++)
                    {
                        MethodBase mb = fs[i] == null ? null : fs[i].GetMethod();
                        if (mb == null) continue;
                        st.Append(mb.DeclaringType == null ? "?" : mb.DeclaringType.Name).Append('.').Append(mb.Name).Append(" <- ");
                    }
                W("用途判定失败（栈里没有 <From...）：" + st);
            }
            else if (!string.IsNullOrEmpty(caller) && _kindProbeCount < 8)
            {
                _kindProbeCount++;
                W("用途判定: " + caller);
            }
        }
        catch { }

        if (caller == null)
        {
            // 栈被线程/异步边界截断时，用入口钩子（PrefixSelectPurpose）记下的用途
            if (!string.IsNullOrEmpty(_pendingPurpose))
            {
                kind = _pendingPurpose;
                caller = "pending";
            }
            return;
        }
        if (caller == "FromDeckForUpgrade" || caller == "FromHandForUpgrade") kind = "upgrade";
        else if (caller == "FromDeckForRemoval") kind = "remove";
        else if (caller == "FromDeckForTransformation") kind = "transform";
        else if (caller == "FromDeckForEnchantment") kind = "enchant";
        else if (caller == "FromHandForDiscard") kind = "discard";
        else if (caller == "FromSimpleGridForRewards") kind = "reward";
        else if (caller == "FromCombatPile") kind = "pile";
        else if (caller == "FromHand") kind = "hand";
        _pendingPurpose = "";
    }

    static string BuildSelectorPayload(List<CardModel> models, string kind, string caller, int min, int max)
    {
        StringBuilder sb = new StringBuilder();
        int id = Interlocked.Increment(ref _seq);
        string chId = CharId();
        bool low = (kind == "remove" || kind == "transform" || kind == "discard");
        sb.Append("{\"id\":").Append(id)
          .Append(",\"kind\":\"pick_cards\",\"min\":").Append(Math.Max(1, min))
          .Append(",\"max\":").Append(Math.Max(1, max <= 0 ? models.Count : max))
          .Append(",\"purpose\":\"").Append(kind)
          .Append("\",\"pick\":\"").Append(low ? "lowest" : "highest")
          .Append("\",\"options\":[");

        for (int i = 0; i < models.Count; i++)
        {
            if (i > 0) sb.Append(',');
            CardModel c = models[i];
            string title = c == null ? "?" : c.Title;
            sb.Append("{\"i\":").Append(i).Append(",\"title\":\"").Append(J(title)).Append('"');
            CardInfo info = FindCardInfo(title, chId);
            if (info != null)
            {
                string key = info.Id + "|" + chId;
                int sc;
                if (_kbScore.TryGetValue(key, out sc))
                {
                    int score = sc;
                    if (kind == "upgrade" || kind == "enchant")
                    {
                        int up;
                        if (_kbUp.TryGetValue(key, out up) && up >= 0) score = up;
                        else
                        {
                            double gain = info.EffUp > info.Eff ? (info.EffUp - info.Eff) : 0;
                            score = (int)Math.Round(sc * 0.35 + gain * 8);
                        }
                    }
                    sb.Append(",\"score\":").Append(score);
                    string tier, kw;
                    if (_kbTier.TryGetValue(key, out tier) && !string.IsNullOrEmpty(tier))
                        sb.Append(",\"tier\":\"").Append(tier).Append('"');
                    if (_kbKw.TryGetValue(key, out kw) && !string.IsNullOrEmpty(kw))
                        sb.Append(",\"kw\":\"").Append(J(kw)).Append('"');
                }
                if (!string.IsNullOrEmpty(info.Desc))
                {
                    string dd = info.Desc.Length > 50 ? info.Desc.Substring(0, 50) : info.Desc;
                    sb.Append(",\"desc\":\"").Append(J(dd)).Append('"');
                }
            }
            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    static List<int> FallbackPicksForModels(List<CardModel> models, string kind, int want)
    {
        List<int> idx = new List<int>();
        List<int> scores = new List<int>();
        string chId = CharId();
        bool low = (kind == "remove" || kind == "transform" || kind == "discard");
        for (int i = 0; i < models.Count; i++)
        {
            idx.Add(i);
            int sc = 40;
            try
            {
                CardInfo info = FindCardInfo(models[i] == null ? "" : models[i].Title, chId);
                if (info != null)
                {
                    string key = info.Id + "|" + chId;
                    int v;
                    if (_kbScore.TryGetValue(key, out v)) sc = v;
                    if (kind == "upgrade" || kind == "enchant")
                    {
                        int up;
                        if (_kbUp.TryGetValue(key, out up) && up >= 0) sc = up;
                    }
                }
            }
            catch { }
            scores.Add(sc);
        }
        idx.Sort(delegate (int a, int b) { return low ? scores[a].CompareTo(scores[b]) : scores[b].CompareTo(scores[a]); });
        List<int> res = new List<int>();
        for (int i = 0; i < idx.Count && res.Count < want; i++) res.Add(idx[i]);
        return res;
    }
}
