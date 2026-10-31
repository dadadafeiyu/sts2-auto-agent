using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Modding;

// 线程边界：后台只做 HTTP；所有游戏对象操作都排队回主线程。
// 决策全交给「StS2 决策器」；模组只算数、只执行。
// 战斗：一次只问一个动作（打哪张、打谁），直到她说"够了"。
// [ModInitializer] 由 SuiteEntry 统一调用
public static partial class AgentSeatEntry
{
    static readonly string Out = Paths.Logs + @"\agentseat.log";
    static readonly string AutoSlayLog = Paths.Logs + @"\autoslay.log";
    const string Base = "http://127.0.0.1:18731";
        // 每局随机种子（不固定，不保留）
    static readonly string Seed = DateTime.Now.ToString("yyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);

    static readonly System.Net.Http.HttpClient Http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    static Assembly _sts2;
    static Harmony _harmony;
    static int _frames, _mainThreadId;
    static bool _installed, _started;
    static int _seq;
    static string _lastAskSig;
    static volatile bool _loopRestartPending;
    static int _loopRestarts;
    static object _slayerInst;   // 她做决定时的局面签名（手牌+能量+星星）

    static readonly ConcurrentQueue<Action> _mainQ = new ConcurrentQueue<Action>();

    [ThreadStatic] static bool _inFallback;

    static int _endedTurn = -1;
    static int _mapCalls;
    static string _mapKey = "";
    static NMapPoint _mapPick = null;
    static bool _mapAsking;
    static DateTime _mapWindow = DateTime.MinValue;

    static Type _pcsType;
    static PropertyInfo _phaseProp, _turnProp;

    static void W(string s) { try { File.AppendAllText(Out, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\r\n", new UTF8Encoding(false)); } catch { } }

    static void RunOnMain(Action a, int timeoutMs)
    {
        if (Thread.CurrentThread.ManagedThreadId == _mainThreadId) { a(); return; }
        ManualResetEventSlim done = new ManualResetEventSlim(false);
        DateTime t0 = DateTime.UtcNow;
        _mainQ.Enqueue(new Action(() =>
        {
            try { a(); }
            catch (Exception e)
            {
                Exception ie = e.InnerException != null ? e.InnerException : e;
                W("主线程任务异常: " + ie.GetType().Name + ": " + ie.Message);
                try { if (ie.StackTrace != null) W("  位置: " + ie.StackTrace.Split('\n')[0]); } catch { }
            }
            finally { done.Set(); }
        }));
        bool ok = done.Wait(timeoutMs);
        int waitMs = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
        if (!ok) W("主线程等待超时 " + waitMs + "ms");
        else if (waitMs > 1000) W("主线程等待 " + waitMs + "ms");
    }
    static void RunOnMain(Action a) { RunOnMain(a, 10000); }

    static Assembly FindSts2()
    {
        if (_sts2 != null) return _sts2;
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        { try { if (a.GetName().Name == "sts2") { _sts2 = a; return _sts2; } } catch { } }
        return null;
    }
    static object Prop(object o, string n)
    {
        if (o == null) return null;
        Type t = o.GetType();
        while (t != null)
        {
            try
            {
                PropertyInfo p = t.GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (p != null) return p.GetValue(o, null);
            }
            catch { }
            t = t.BaseType;
        }
        return null;
    }
    static object Field(object o, string n)
    {
        if (o == null) return null;
        Type t = o.GetType();
        while (t != null)
        {
            try
            {
                FieldInfo f = t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
            }
            catch { }
            t = t.BaseType;
        }
        return null;
    }
    static string S(object o) { return o == null ? "" : Convert.ToString(o); }
    static int I(object o, string n) { try { object v = Prop(o, n); return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; } }
    static List<object> L(object seq) { List<object> r = new List<object>(); IEnumerable e = seq as IEnumerable; if (e != null) foreach (object o in e) r.Add(o); return r; }
    static string J(string s)
    {
        if (s == null) return "";
        StringBuilder b = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') b.Append("\\\"");
            else if (c == '\\') b.Append("\\\\");
            else if (c == '\n' || c == '\r' || c == '\t') b.Append(' ');
            else if (c < 0x20) b.Append(' ');
            else b.Append(c);
        }
        return b.ToString();
    }

    static object Cm()
    {
        Type t = _sts2.GetType("MegaCrit.Sts2.Core.Combat.CombatManager", false);
        return t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
    }
    static object StateNow() { object cm = Cm(); return cm == null ? null : Field(Prop(cm, "StateTracker"), "_state"); }
    static object Player0()
    {
        IList ps = Prop(StateNow(), "Players") as IList;
        return ps == null || ps.Count == 0 ? null : ps[0];
    }
    static object Sync()
    {
        Type t = _sts2.GetType("MegaCrit.Sts2.Core.Runs.RunManager", false);
        object rm = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
        return rm == null ? null : Prop(rm, "ActionQueueSynchronizer");
    }
    static string PhaseText(object pcs)
    {
        if (_pcsType == null)
        {
            _pcsType = pcs.GetType();
            _phaseProp = _pcsType.GetProperty("Phase", BindingFlags.Public | BindingFlags.Instance);
            _turnProp = _pcsType.GetProperty("TurnNumber", BindingFlags.Public | BindingFlags.Instance);
        }
        object v = _phaseProp == null ? null : _phaseProp.GetValue(pcs, null);
        return v == null ? "" : v.ToString();
    }
    static int TurnNo(object pcs)
    {
        if (_pcsType == null) PhaseText(pcs);
        object v = _turnProp == null ? null : _turnProp.GetValue(pcs, null);
        try { return v == null ? -1 : Convert.ToInt32(v); } catch { return -1; }
    }

    static int PowerAmount(object creature, string[] titles)
    {
        try
        {
            IEnumerable pseq = (Prop(creature, "Powers") ?? Field(creature, "_powers")) as IEnumerable;
            if (pseq == null) return 0;
            foreach (object p in pseq)
            {
                if (p == null) continue;
                // Title 是 LocString，要 Loc 才拿到「力量」这种文本；再兜底查 Id
                string t = Loc(Prop(p, "Title"));
                if (string.IsNullOrEmpty(t)) t = S(Prop(p, "Title"));
                string id = S(Prop(p, "Id"));
                for (int i = 0; i < titles.Length; i++)
                {
                    if (!string.IsNullOrEmpty(t) && t.IndexOf(titles[i], StringComparison.OrdinalIgnoreCase) >= 0)
                        return I(p, "Amount");
                    if (!string.IsNullOrEmpty(id) && id.IndexOf(titles[i], StringComparison.OrdinalIgnoreCase) >= 0)
                        return I(p, "Amount");
                }
            }
        }
        catch { }
        return 0;
    }

    static double VarValue(object v)
    {
        try { object iv = Prop(v, "IntValue"); return iv == null ? 0 : Convert.ToDouble(iv); } catch { return 0; }
    }

    static void CardNumbers(object card, object player, object state, out int dmg, out int hits, out int block, out bool exact)
    {
        dmg = 0; hits = 1; block = 0; exact = true;
        try
        {
            object dv = Prop(card, "DynamicVars");
            object dmgVar = Prop(dv, "Damage");
            object blkVar = Prop(dv, "Block");

            hits = Math.Max(1, I(card, "BaseReplayCount"));
            object xcost = Prop(card, "HasEnergyCostX");
            if (xcost is bool && (bool)xcost) exact = false;
            if (Prop(dv, "CalculatedDamage") != null) exact = false;

            int strength = PowerAmount(Prop(player, "Creature"), new string[] { "力量", "Strength" });
            int dexterity = PowerAmount(Prop(player, "Creature"), new string[] { "敏捷", "Dexterity" });

            // 先让游戏自己算（预览值），算不出来才用我们的公式
            if (dmgVar != null)
            {
                int byGame = PreviewCardVar(card, dmgVar);
                if (byGame >= 0) dmg = byGame;
                else
                {
                    bool vuln = false, weak = false;
                    foreach (object e in L(Prop(state, "Enemies")))
                    {
                        if (PowerAmount(e, new string[] { "易伤", "Vulnerable" }) > 0) vuln = true;
                        if (PowerAmount(e, new string[] { "虚弱", "Weak" }) > 0) weak = true;
                    }
                    double v = (VarValue(dmgVar) + strength) * (vuln ? 1.5 : 1.0) * (weak ? 0.75 : 1.0);
                    dmg = (int)Math.Floor(v);
                    exact = false;
                }
            }
            if (blkVar != null)
            {
                int byGameB = PreviewCardVar(card, blkVar);
                if (byGameB >= 0) block = byGameB;
                else
                {
                    block = (int)Math.Floor(VarValue(blkVar) + dexterity);
                    exact = false;
                }
            }
            ClearCardPreview(card);
        }
        catch { exact = false; }
    }

    // 返回牌费；X 费/读不到返回 -1（视为可打）
    static int CardCost(object card)
    {
        try
        {
            object ec = Prop(card, "EnergyCost");
            if (ec == null) return 0;
            // 先用游戏自己的"这回合实际要花多少"（含临时增减费）
            MethodInfo m = ec.GetType().GetMethod("GetAmountToSpend", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (m != null && m.GetParameters().Length == 0)
            {
                object real = m.Invoke(ec, null);
                if (real != null) return Convert.ToInt32(real);
            }
            string s = S(Prop(ec, "Canonical") ?? Prop(ec, "Current") ?? ec);
            int v;
            if (int.TryParse(s.Trim(), out v)) return v;
            return -1;
        }
        catch { return -1; }
    }



    // 卡牌的星星消耗（没有则 0）
    static int CardStarCost(object card)
    {
        try
        {
            string[] names = new string[] { "CurrentStarCost", "CanonicalStarCost", "BaseStarCost" };
            for (int i = 0; i < names.Length; i++)
            {
                object v = Prop(card, names[i]);
                if (v == null) continue;
                int n;
                if (int.TryParse(Convert.ToString(v), out n) && n > 0) return n;
            }
        }
        catch { }
        return 0;
    }

    // 手上有能打的牌吗（费用 <= 能量，或 0 费/X 费）
    // 游戏自带的"能不能打"判断（带原因）
    static MethodInfo _canPlayMi;
    static bool CardCanPlay(object card, out string reason)
    {
        reason = "";
        try
        {
            if (_canPlayMi == null)
            {
                foreach (MethodInfo m in card.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name != "CanPlay") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType.IsByRef && ps[1].ParameterType.IsByRef)
                    {
                        _canPlayMi = m;
                        break;
                    }
                }
            }
            if (_canPlayMi == null) return true;

            ParameterInfo[] pp = _canPlayMi.GetParameters();
            Type rt = pp[0].ParameterType.GetElementType();
            object[] args = new object[] { Activator.CreateInstance(rt), null };
            object r = _canPlayMi.Invoke(card, args);
            if (r is bool && (bool)r) return true;
            reason = Convert.ToString(args[0]);
            if (reason == "None") reason = "";
            return false;
        }
        catch { return true; }
    }

    // 局面签名：手牌 + 能量 + 星星（用于判断"她的决定之后有没有新牌进来"）
    static string CombatSig()
    {
        string sig = "";
        RunOnMain(new Action(() =>
        {
            try
            {
                object p = Player0();
                object pcs = p == null ? null : Prop(p, "PlayerCombatState");
                if (pcs == null) return;
                StringBuilder sb = new StringBuilder();
                foreach (object c in L(Prop(Prop(pcs, "Hand"), "Cards")))
                    sb.Append(S(Prop(c, "Title"))).Append('|');
                sb.Append('#').Append(I(pcs, "Energy")).Append('#').Append(I(pcs, "Stars"));
                sig = sb.ToString();
            }
            catch { }
        }));
        return sig;
    }

    static bool HasPlayable(object pcs)
    {
        foreach (object c in L(Prop(Prop(pcs, "Hand"), "Cards")))
        {
            string why;
            if (CardCanPlay(c, out why)) return true;
        }
        return false;
    }

    // 游戏自带的资源判断（同时考虑能量与星星）
    static bool ResourcesEnough(object pcs, object card)
    {
        try
        {
            MethodInfo m = pcs.GetType().GetMethod("HasEnoughResourcesFor", BindingFlags.Public | BindingFlags.Instance);
            if (m == null) return false;
            ParameterInfo[] ps = m.GetParameters();
            if (ps.Length != 1) return false;
            object r = m.Invoke(pcs, new object[] { card });
            return r is bool && (bool)r;
        }
        catch { return false; }
    }

    // 一次性探测：敌人意图的真实结构
    static void ProbeIntent(List<object> enemies)
    {
        try
        {
            for (int i = 0; i < enemies.Count && i < 2; i++)
            {
                object e = enemies[i];
                object mon = Prop(e, "Monster");
                object move = Prop(mon, "NextMove");
                StringBuilder sb = new StringBuilder();
                sb.Append("敌人[").Append(S(Prop(e, "Name"))).Append("] ");
                sb.Append("move=").Append(move == null ? "null" : move.GetType().Name).Append(" ");
                if (move != null)
                {
                    List<object> intents = L(Prop(move, "Intents"));
                    sb.Append("intents=").Append(intents.Count).Append(" ");
                    for (int k = 0; k < intents.Count && k < 3; k++)
                    {
                        object it = intents[k];
                        sb.Append("{").Append(it.GetType().Name).Append(": ");
                        foreach (FieldInfo fi in it.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (fi.Name.IndexOf("k__BackingField") < 0 && fi.Name[0] == '<') continue;
                            object v = null;
                            try { v = fi.GetValue(it); } catch { }
                            if (v is int || v is bool || v is string) sb.Append(fi.Name).Append('=').Append(v).Append(' ');
                        }
                        foreach (PropertyInfo pi in it.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (pi.Name.IndexOf("Damage") < 0 && pi.Name.IndexOf("Value") < 0 && pi.Name.IndexOf("Type") < 0) continue;
                            object v = null;
                            try { v = pi.GetValue(it, null); } catch { }
                            sb.Append(pi.Name).Append('=').Append(Convert.ToString(v)).Append(' ');
                        }
                        sb.Append("} ");
                    }
                }
                W("意探: " + sb.ToString());
            }
        }
        catch (Exception ex) { W("意探失败: " + ex.Message); }
    }

    // 把威胁合成一句人话：要挨多少、还差多少格挡
    static string ThreatText(List<object> enemies, object player)
    {
        int incoming = IncomingTotal(enemies, player);
        if (incoming <= 0) return "敌人这回合不攻击";
        int block = 0;
        try { block = I(Prop(player, "Creature"), "Block"); } catch { }
        int need = Math.Max(0, incoming - block);
        if (need <= 0) return "将受到 " + incoming + " 点伤害，现有护甲 " + block + " 已够挡";
        return "将受到 " + incoming + " 点伤害，现有护甲 " + block + "，还需挡 " + need + " 点";
    }

    // 所有敌人本回合意图伤害合计（已含易伤/虚弱修正）
    static int IncomingTotal(List<object> enemies, object player)
    {
        int total = 0;
        for (int i = 0; i < enemies.Count; i++)
        {
            total += EnemyIntentTotal(enemies[i], player);
        }
        return total;
    }


    // 单个敌人的意图伤害：攻击意图调用 DamageCalc，其余算 0
    // 敌人这回合的意图总伤害：只信任游戏自己的 GetTotalDamage（UI 显示用的就是它）
    // 读不到才退回 DamageCalc（原始值，标记为估算）
    static int EnemyIntentTotal(object enemy, object player)
    {
        try
        {
            object move = Prop(Prop(enemy, "Monster"), "NextMove");
            List<object> intents = L(Prop(move, "Intents"));
            if (intents.Count == 0) return 0;

            int realTotal = -1;
            for (int k = 0; k < intents.Count; k++)
            {
                int one = IntentRealDamage(intents[k], enemy, player);
                if (one >= 0) realTotal = (realTotal < 0 ? 0 : realTotal) + one;
            }
            if (realTotal >= 0) return realTotal;

            // 兜底：原始伤害 × 段数（不含任何修正，调用方应当知道这是估算）
            int raw = 0;
            for (int i = 0; i < intents.Count; i++)
            {
                object it = intents[i];
                string t = Convert.ToString(Prop(it, "IntentType"));
                if (t == null || t.IndexOf("Attack", StringComparison.OrdinalIgnoreCase) < 0) continue;
                object calc = Prop(it, "DamageCalc");
                if (calc == null) calc = Field(it, "<DamageCalc>k__BackingField");
                if (calc == null) continue;
                int rep = 1;
                try { object r = Prop(it, "Repeats"); if (r != null) rep = Math.Max(1, Convert.ToInt32(r)); } catch { }
                try
                {
                    MethodInfo inv = calc.GetType().GetMethod("Invoke");
                    object v = inv.Invoke(calc, null);
                    raw += (int)Math.Floor(Convert.ToDecimal(v)) * rep;
                }
                catch { }
            }
            return raw;
        }
        catch { return 0; }
    }

    // 手牌是否还在变（发牌/抽牌动画没播完时模型里张数会跳）
    static int _hwCount = -1, _hwTurn = -1;
    static long _hwChangedMs;
    static bool _hwSeen;
    const int HandSettleMs = 250;

    static void TickHandWatch()
    {
        try
        {
            object player = Player0();
            object pcs = player == null ? null : Prop(player, "PlayerCombatState");
            if (pcs == null) return;
            int count = L(Prop(Prop(pcs, "Hand"), "Cards")).Count;
            int turn = I(pcs, "TurnNumber");
            long now = System.Environment.TickCount64;
            if (!_hwSeen || _hwCount != count || _hwTurn != turn)
            {
                _hwSeen = true; _hwCount = count; _hwTurn = turn; _hwChangedMs = now;
            }
        }
        catch { }
    }

    static bool HandComplete()
    {
        if (!_hwSeen) return true;
        return System.Environment.TickCount64 - _hwChangedMs >= HandSettleMs;
    }

    // 玩家自己的能力（力量/敏捷/毒…）：照 dsh-sts2-main 的 PlayerDto，直接发给 agent 自己算
    // 玩家身上的全部能力（力量/敏捷/毒/易伤…）：名字 + 层数 + 说明，照敌人的读法来
    static void AppendPlayerPowersJson(StringBuilder sb, object player)
    {
        sb.Append(",\"powers\":[");
        int n = 0;
        try
        {
            object cre = Prop(player, "Creature");
            IEnumerable pseq = (Prop(cre, "Powers") ?? Field(cre, "_powers")) as IEnumerable;
            if (pseq != null)
            {
                foreach (object pw in pseq)
                {
                    if (pw == null) continue;
                    string nm = Loc2(Prop(pw, "Title"));
                    if (string.IsNullOrEmpty(nm)) nm = S(Prop(pw, "Id"));
                    int amt = I(pw, "Amount");
                    if (string.IsNullOrEmpty(nm) || amt == 0) continue;
                    if (n > 0) sb.Append(',');
                    sb.Append("{\"n\":\"").Append(J(nm)).Append("\",\"a\":").Append(amt);
                    sb.Append('}');
                    n++;
                    if (n >= 12) break;
                }
            }
        }
        catch (Exception e) { W("读玩家能力失败: " + e.Message); }
        sb.Append(']');
    }

    // 卡牌的"游戏算法数值"：走 DynamicVar.UpdateCardPreview（= NCard.UpdateVisuals 画卡面时同一条路径）
    //   内部：BaseValue(+附魔) → Hook.ModifyDamage / Hook.ModifyBlock → PreviewValue
    //   target 传 null = 不对着具体敌人（= 牌组界面口径：含力量/敏捷/附魔，不含目标易伤）
    // 返回值 < 0 表示读不到
    static int PreviewCardVar(object card, object var)
    {
        if (card == null || var == null) return -1;
        try
        {
            MethodInfo upd = null;
            foreach (MethodInfo mi in var.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (mi.Name != "UpdateCardPreview") continue;
                ParameterInfo[] ps = mi.GetParameters();
                if (ps.Length == 4 && ps[3].ParameterType == typeof(bool)) { upd = mi; break; }
            }
            if (upd == null) { HookWarnOnce("找不到 UpdateCardPreview"); return -1; }

            ParameterInfo[] up = upd.GetParameters();
            object previewMode = Enum.ToObject(up[1].ParameterType, 1);   // CardPreviewMode.Normal
            upd.Invoke(var, new object[] { card, previewMode, null, true });

            object pv = Prop(var, "PreviewValue");
            if (pv == null) return -1;
            return (int)Math.Round(Convert.ToDecimal(pv));
        }
        catch (Exception e)
        {
            Exception ie = e.InnerException != null ? e.InnerException : e;
            HookWarnOnce("卡牌预览失败: " + ie.GetType().Name + ": " + ie.Message);
            return -1;
        }
    }

    // 读完清掉预览值，避免影响别的显示
    static void ClearCardPreview(object card)
    {
        try
        {
            object dv = Prop(card, "DynamicVars");
            if (dv == null) return;
            MethodInfo clear = dv.GetType().GetMethod("ClearPreview", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (clear != null && clear.GetParameters().Length == 0) clear.Invoke(dv, null);
        }
        catch { }
    }

    static int _cardHookWarn;
    static void HookWarnOnce(string msg)
    {
        if (_cardHookWarn >= 3) return;
        _cardHookWarn++;
        W(msg);
    }

    // 卡牌的全部动态变量（照 dsh-sts2-main 的 VarsOf：不筛选，全给）
    static string VarsJson(object card)
    {
        StringBuilder sb = new StringBuilder();
        try
        {
            object dv = Prop(card, "DynamicVars");
            if (dv == null) return "";
            IEnumerable vals = Prop(dv, "Values") as IEnumerable;
            if (vals == null) return "";
            sb.Append(",\"vars\":{");
            bool first = true;
            foreach (object v in vals)
            {
                try
                {
                    string nm = Convert.ToString(Prop(v, "Name"));
                    if (string.IsNullOrEmpty(nm)) continue;
                    // Damage/Block 已用 dmg/block 字段给出（游戏算的），不再重复
                    if (nm == "Damage" || nm == "Block") continue;
                    int iv = I(v, "IntValue");
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(nm).Append("\":").Append(iv);
                }
                catch { }
            }
            sb.Append('}');
            return first ? "" : sb.ToString();
        }
        catch { return ""; }
    }

    // 真实费用：读 GetAmountToSpend()（含临时增减费），读不到退回 Canonical
    static int CardCostReal(object card)
    {
        try
        {
            object ec = Prop(card, "EnergyCost");
            if (ec == null) return 0;
            MethodInfo m = ec.GetType().GetMethod("GetAmountToSpend", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (m != null && m.GetParameters().Length == 0)
            {
                object v = m.Invoke(ec, null);
                if (v != null) return Convert.ToInt32(v);
            }
            return I(ec, "Canonical");
        }
        catch { return 0; }
    }

    // 敌人意图的"打到身上"的真实伤害：用游戏自己的 GetTotalDamage（含力量/虚弱等全部修正）
    static int IntentRealDamage(object intent, object enemy, object player)
    {
        try
        {
            MethodInfo m = null;
            foreach (MethodInfo mi in intent.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (mi.Name == "GetTotalDamage") { m = mi; break; }
            if (m == null) return -1;
            ParameterInfo[] ps = m.GetParameters();
            if (ps.Length != 2) return -1;

            object creature = Prop(player, "Creature");
            if (creature == null) return -1;
            Type listType = typeof(List<>).MakeGenericType(ps[0].ParameterType.GetGenericArguments()[0]);
            System.Collections.IList targets = (System.Collections.IList)Activator.CreateInstance(listType);
            targets.Add(creature);
            object r = m.Invoke(intent, new object[] { targets, enemy });
            return r == null ? -1 : Convert.ToInt32(r);
        }
        catch { return -1; }
    }

    // 敌人意图：算好总伤害与打穿护甲后的实际掉血
    // 规则：易伤在"受到伤害"的一方 ×1.5；虚弱在"造成伤害"的一方 ×0.75
    static string IntentJson(object enemy, object player)
    {
        try
        {
            object move = Prop(Prop(enemy, "Monster"), "NextMove");
            List<object> intents = L(Prop(move, "Intents"));
            int hits = 0, perHit = 0, rawTotal = 0;
            for (int i = 0; i < intents.Count; i++)
            {
                object it = intents[i];
                // 攻击伤害在 DamageCalc（Func<decimal>）里；MultiAttackIntent 用 Repeats 表示段数（3x3）
                int v = 0;
                try
                {
                    object calc = Prop(it, "DamageCalc");
                    if (calc is Delegate) v = (int)Math.Floor(Convert.ToDecimal(((Delegate)calc).DynamicInvoke()));
                }
                catch { }
                if (v <= 0)
                {
                    object dmg = Prop(it, "Damage") ?? Prop(it, "Value") ?? Prop(it, "Amount");
                    try { v = dmg == null ? 0 : Convert.ToInt32(dmg); } catch { v = 0; }
                }
                if (v <= 0) continue;
                int rep = 1;
                try { object r = Prop(it, "Repeats"); if (r != null) rep = Math.Max(1, Convert.ToInt32(r)); } catch { }
                if (hits == 0) perHit = v;
                rawTotal += v * rep;
                hits += rep;
            }
            if (hits == 0) return ",\"intent\":{\"type\":\"none\",\"total\":0,\"hits\":0,\"afterBlock\":0}";

            int pBlock = 0, pVuln = 0, eWeak = 0;
            try
            {
                object me = Prop(player, "Creature");
                pBlock = I(me, "Block");
                pVuln = PowerAmount(me, new string[] { "易伤", "Vulnerable" });
                eWeak = PowerAmount(enemy, new string[] { "虚弱", "Weak" });
            }
            catch { }

            // total 只认游戏算的（GetTotalDamage）；拿不到才退回原始值
            int gameTotal = -1;
            try
            {
                for (int k = 0; k < intents.Count; k++)
                {
                    int one = IntentRealDamage(intents[k], enemy, player);
                    if (one >= 0) gameTotal = (gameTotal < 0 ? 0 : gameTotal) + one;
                }
            }
            catch { }
            int totalInt = gameTotal >= 0 ? gameTotal : rawTotal;
            int afterBlock = Math.Max(0, totalInt - pBlock);

            StringBuilder sb = new StringBuilder();
            sb.Append(",\"intent\":{\"type\":\"attack\",\"total\":").Append(totalInt)
              .Append(",\"perHit\":").Append(perHit)
              .Append(",\"hits\":").Append(hits)
              .Append(",\"afterBlock\":").Append(afterBlock);
            if (pVuln > 0) sb.Append(",\"iAmVulnerable\":true");
            if (eWeak > 0) sb.Append(",\"enemyIsWeak\":true");
            sb.Append('}');
            return sb.ToString();
        }
        catch { return ",\"intent\":{\"type\":\"none\",\"total\":0,\"hits\":0,\"afterBlock\":0}"; }
    }


    static string Post(string json)
    {
        StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
        HttpResponseMessage resp = Http.PostAsync(Base + "/decide", content).GetAwaiter().GetResult();
        string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode) throw new Exception("HTTP " + (int)resp.StatusCode + ": " + body);
        return body;
    }
    static string Ask(string json, string tag)
    {
        Exception last = null;
        for (int i = 0; i < 2; i++)
        {
            try { return Post(json); }
            catch (Exception e) { last = e; W(tag + ": 第 " + (i + 1) + " 次失败 " + e.Message); Thread.Sleep(500); }
        }
        throw new Exception("决策器不可用", last);
    }


    // ---------- 商店接管（照抄 ShopRoomHandler，只把"随机买"换成"她选"）----------
    // LocString -> 显示文本
    static string Loc(object o)
    {
        if (o == null) return "";
        try
        {
            if (o.GetType().Name == "LocString")
            {
                MethodInfo m = o.GetType().GetMethod("GetFormattedText", BindingFlags.Public | BindingFlags.Instance);
                if (m != null)
                {
                    object v = m.Invoke(o, m.GetParameters().Length == 0 ? null : new object[] { false });
                    if (v != null) return Convert.ToString(v);
                }
            }
        }
        catch { }
        return Convert.ToString(o);
    }

    // 非战斗场景用这个取玩家（CombatManager 的状态在房间外是空的）
    static object RunPlayer()
    {
        try
        {
            Type t = _sts2.GetType("MegaCrit.Sts2.Core.Runs.RunManager", false);
            object rm = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
            if (rm != null)
            {
                object st = Prop(rm, "State") ?? Field(rm, "<State>k__BackingField");
                IList ps = Prop(st, "Players") as IList;
                if (ps != null && ps.Count > 0) return ps[0];
            }
        }
        catch { }
        return Player0();
    }

    // 金币：属性读不到就读 _gold 字段
    static int GoldOf()
    {
        try
        {
            object p0 = RunPlayer();
            if (p0 == null) return 0;
            object g = Prop(p0, "Gold");
            if (g != null) return Convert.ToInt32(g);
            object f = Field(p0, "_gold");
            return f == null ? 0 : Convert.ToInt32(f);
        }
        catch { return 0; }
    }

    // 把 {Damage:diff()} 这类占位符换成真实数值
    static string FillPlaceholders(string desc, object model)
    {
        if (string.IsNullOrEmpty(desc)) return desc;
        try
        {
            desc = desc.Replace("[gold]", "").Replace("[/gold]", "");
            object dv = Prop(model, "DynamicVars");
            desc = Regex.Replace(desc, "\\{(\\w+):diff\\(\\)\\}", delegate (Match m)
            {
                string key = m.Groups[1].Value;
                object var = dv == null ? null : Prop(dv, key);
                if (var == null) return "?";
                double v = VarValue(var);
                return ((int)v).ToString();
            });
        }
        catch { }
        return desc;
    }

    // 取出商店条目的模型 id（用于查分）
    // 商店格子分类：relic / card / removal / potion
    static string ClassifyShopSlot(object slot)
    {
        try
        {
            string stn = slot.GetType().Name;
            if (stn.IndexOf("Removal", StringComparison.OrdinalIgnoreCase) >= 0) return "removal";
            if (stn.IndexOf("Potion", StringComparison.OrdinalIgnoreCase) >= 0) return "potion";
            object m = Prop(Prop(slot, "Entry"), "Model");
            string id = m == null ? "" : Convert.ToString(Prop(m, "Id"));
            if (id.StartsWith("RELIC.", StringComparison.OrdinalIgnoreCase)) return "relic";
            if (id.StartsWith("CARD.", StringComparison.OrdinalIgnoreCase)) return "card";
            if (id.StartsWith("POTION.", StringComparison.OrdinalIgnoreCase)) return "potion";
            return "card";
        }
        catch { return "card"; }
    }

    static string ModelInfoId(object entry)
    {
        try
        {
            object m = Prop(entry, "Model");
            if (m == null) m = Prop(Prop(entry, "CreationResult"), "Card");
            if (m == null) m = Prop(entry, "Card");
            if (m == null) return "";
            return Convert.ToString(Prop(m, "Id"));
        }
        catch { return ""; }
    }

    // 商店商品信息：把卡/遗物/药水的名字和效果取出来
    // 群伤判定（按描述文本）
    static bool CardIsAoe(object card)
    {
        try
        {
            string d = Loc2(Prop(card, "Description"));
            return d.IndexOf("所有敌人") >= 0 || d.IndexOf("每名敌人") >= 0 || d.IndexOf("每个敌人") >= 0;
        }
        catch { return false; }
    }

    static string Loc2(object o)
    {
        try
        {
            if (o != null && o.GetType().Name == "LocString")
            {
                MethodInfo m = o.GetType().GetMethod("GetFormattedText", BindingFlags.Public | BindingFlags.Instance);
                if (m != null)
                {
                    object v = m.Invoke(o, m.GetParameters().Length == 0 ? null : new object[] { false });
                    if (v != null) return Convert.ToString(v);
                }
            }
        }
        catch { }
        return o == null ? "" : Convert.ToString(o);
    }

    static string ModelInfo(object entry)
    {
        try
        {
            object m = Prop(entry, "Model");
            if (m == null) m = Prop(Prop(entry, "CreationResult"), "Card");
            if (m == null) m = Prop(entry, "Card");
            if (m == null) return "";
            string title = Loc(Prop(m, "Title"));
            string desc = FillPlaceholders(Loc(Prop(m, "Description")), m);
            if (desc.Length == 0) desc = FillPlaceholders(Loc(Prop(m, "SmartDescription")), m);
            if (desc.Length > 70) desc = desc.Substring(0, 70);
            string text = (title.Length > 0 ? title : m.GetType().Name) + (desc.Length > 0 ? "：" + desc : "");
            if (text.Length > 90) text = text.Substring(0, 90);
            return text;
        }
        catch { return ""; }
    }

    static async Task RunShopAsync()
    {
        await Task.Yield();
        try
        {
            SceneTree tree = Engine.GetMainLoop() as SceneTree;
            if (tree == null) return;
            Node roomNode = null;
            for (int i = 0; i < 100; i++)
            {
                RunOnMain(new Action(() =>
                {
                    try { roomNode = tree.Root.GetNodeOrNull("/root/Game/RootSceneContainer/Run/RoomContainer/MerchantRoom"); }
                    catch { roomNode = null; }
                }));
                if (roomNode != null) break;
                await Task.Delay(150);
            }
            if (roomNode == null) { W("商店: 等不到房间节点"); return; }

            Type rnType = roomNode.GetType();
            W("商店: 打开货架");
            RunOnMain(new Action(() => rnType.GetMethod("OpenInventory").Invoke(roomNode, null)));
            await Task.Delay(600);

            int bought = 0;
            for (int round = 0; round < 50; round++)
            {
                object inventory = Prop(roomNode, "Inventory");
                List<object> slots = new List<object>();
                List<string> descs = new List<string>();
                List<object> entries = new List<object>();
                List<string> kinds = new List<string>();
                RunOnMain(new Action(() =>
                {
                    object all = inventory == null ? null : inventory.GetType().GetMethod("GetAllSlots").Invoke(inventory, null);
                    IEnumerable seq = all as IEnumerable;
                    if (seq == null) return;
                    foreach (object slot in seq)
                    {
                        if (slot == null) continue;
                        string stn = slot.GetType().Name;
                        object entry = Prop(slot, "Entry");
                        if (entry == null) continue;
                        object st = Prop(entry, "IsStocked");
                        object en = Prop(entry, "EnoughGold");
                        if (!(st is bool) || !(bool)st) continue;
                        if (!(en is bool) || !(bool)en) continue;
                        slots.Add(slot);
                        entries.Add(entry);
                        kinds.Add(ClassifyShopSlot(slot));
                        string info = ModelInfo(entry);
                        if (info.Length == 0) info = slot.GetType().Name;
                        if (kinds[kinds.Count - 1] == "removal")
                        {
                            string rt = Loc(Prop(slot, "Title"));
                            if (string.IsNullOrEmpty(rt)) rt = "移除一张牌";
                            info = rt;
                        }
                        descs.Add(S(Prop(entry, "Cost")) + "金/" + info);
                    }
                }));

                if (slots.Count == 0) { W("商店: 没有买得起的了（已买 " + bought + " 件）"); break; }

                int pick = -1;
                try
                {
                    int id = Interlocked.Increment(ref _seq);
                    StringBuilder sb = new StringBuilder();
                    sb.Append("{\"id\":").Append(id).Append(",\"kind\":\"pick_shop\",\"gold\":").Append(GoldOf());
                    sb.Append(",\"options\":[");
                    for (int i = 0; i < slots.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append("{\"i\":").Append(i).Append(",\"info\":\"").Append(J(descs[i])).Append('"')
                          .Append(",\"kind\":\"").Append(kinds[i]).Append('"');
                        string mid = ModelInfoId(entries[i]);
                        int msc = 0;
                        string mtier = null;
                        bool got = false;
                        if (!string.IsNullOrEmpty(mid))
                        {
                            if (_kbScore.TryGetValue(mid + "|" + CharId(), out msc)) got = true;
                            else if (_relicScore.TryGetValue(mid, out msc)) got = true;
                        }
                        if (got)
                        {
                            sb.Append(",\"score\":").Append(msc);
                            if (!string.IsNullOrEmpty(mid) && _kbTier.TryGetValue(mid + "|" + CharId(), out mtier))
                                sb.Append(",\"tier\":\"").Append(mtier).Append('"');
                            else if (!string.IsNullOrEmpty(mid) && _relicTier.TryGetValue(mid, out mtier))
                                sb.Append(",\"tier\":\"").Append(mtier).Append('"');
                        }
                        int prio = 3;
                        if (kinds[i] == "potion") prio = 2;
                        else if (kinds[i] == "relic") prio = 4;
                        else if (kinds[i] == "card" && got && msc >= 85) prio = 4;
                        sb.Append(",\"prio\":").Append(prio);
                        sb.Append('}');
                    }
                    sb.Append("]}");
                    string board = sb.ToString();
                    W("商店看板: " + board.Substring(0, Math.Min(400, board.Length)));
                    string resp = Ask(board, "pick_shop");
                    Match m = Regex.Match(resp ?? "", "\"index\"\\s*:\\s*(-?\\d+)");
                    if (m.Success) pick = int.Parse(m.Groups[1].Value);
                    if (resp != null && Regex.IsMatch(resp, "\"skip\"\\s*:\\s*true")) { W("商店: 她说不用买了"); break; }
                }
                catch (Exception e) { W("商店: 决策失败 " + e.Message); break; }

                if (pick < 0 || pick >= slots.Count) { W("商店: 无效选择，停手"); break; }

                W("商店: 买第 " + pick + " 件（" + descs[pick] + "）");
                object chosen = entries[pick];
                object inv = inventory;
                string err = null;
                RunOnMain(new Action(() =>
                {
                    try
                    {
                        // 精确挑重载：GetMethod 只按名字会撞 "Ambiguous match found"（删牌服务有多个重载）
                        MethodInfo m = null;
                        foreach (MethodInfo mi in chosen.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            if (mi.Name != "OnTryPurchaseWrapper") continue;
                            ParameterInfo[] ps0 = mi.GetParameters();
                            if (ps0.Length == 2) { m = mi; break; }
                            if (m == null) m = mi;
                        }
                        if (m == null) { err = "找不到 OnTryPurchaseWrapper"; return; }
                        ParameterInfo[] mp = m.GetParameters();
                        object[] args = new object[mp.Length];
                        for (int q = 0; q < mp.Length; q++)
                        {
                            if (q == 0) args[q] = Prop(inv, "Inventory");
                            else if (mp[q].ParameterType == typeof(bool)) args[q] = false;
                            else args[q] = null;
                        }
                        object r = m.Invoke(chosen, args);
                    }
                    catch (Exception e) { err = e.InnerException != null ? e.InnerException.Message : e.Message; }
                }));
                if (err != null)
                {
                    W("商店: 购买报错 " + err);
                    _shopFail[pick] = (_shopFail.ContainsKey(pick) ? _shopFail[pick] : 0) + 1;
                    if (_shopFail[pick] >= 2) { W("商店: 第 " + pick + " 件连续失败 " + _shopFail[pick] + " 次，本轮跳过"); }
                    await Task.Delay(600);
                    continue;
                }
                _shopFail[pick] = 0;
                bought++;
                await Task.Delay(1500);
            }

            RunOnMain(new Action(() =>
            {
                try
                {
                    object back = null;
                    Type btype = null;
                    foreach (Type t in FindSts2().GetTypes()) { if (t != null && t.Name == "NBackButton") { btype = t; break; } }
                    if (btype != null)
                    {
                        Node found = FindNodeOfType(roomNode, btype.Name);
                        back = found;
                    }
                    if (back != null) ForceClickNode(back);
                }
                catch (Exception e) { W("商店: 关界面失败 " + e.Message); }
            }));
            await Task.Delay(500);

            RunOnMain(new Action(() =>
            {
                try
                {
                    object proceed = Prop(roomNode, "ProceedButton");
                    if (proceed != null) ForceClickNode(proceed);
                }
                catch (Exception e) { W("商店: 点继续失败 " + e.Message); }
            }));
            W("商店: 结束");
        }
        catch (Exception e) { W("商店异常: " + e.Message); }
    }

    static Node FindNodeOfType(Node n, string typeName)
    {
        if (n == null) return null;
        if (n.GetType().Name == typeName)
        {
            CanvasItem ci = n as CanvasItem;
            if (ci == null || ci.IsVisibleInTree()) return n;
        }
        int c = 0;
        try { c = n.GetChildCount(); } catch { return null; }
        for (int i = 0; i < c; i++)
        {
            Node ch = null;
            try { ch = n.GetChild(i); } catch { }
            Node hit = FindNodeOfType(ch, typeName);
            if (hit != null) return hit;
        }
        return null;
    }

    static void ForceClickNode(object node)
    {
        try
        {
            _inFallback = true;
            MethodInfo fc = node.GetType().GetMethod("ForceClick", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fc == null) { W("找不到 ForceClick"); return; }
            fc.Invoke(node, fc.GetParameters().Length == 0 ? null : new object[] { false });
        }
        catch (Exception e) { W("点击失败 " + e.Message); }
        finally { _inFallback = false; }
    }

    static bool PrefixShopHandle(ref Task __result)
    {
        if (!AgentMode()) return true;
        if (_inFallback) return true;
        __result = Task.Run(new Func<Task>(RunShopAsync));
        return false;
    }


    // ---------- 长超时：把 25 分钟的总时限换成 3 小时 ----------
    static async Task LongRun(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            cts.CancelAfter(TimeSpan.FromHours(3));
            Task work = action(cts.Token);
            Task guard = Task.Delay(Timeout.Infinite, cts.Token);
            Task done = await Task.WhenAny(work, guard);
            if (done == guard) throw new TimeoutException("决策式对局超时（3 小时）");
            await work;
        }
        finally { cts.Dispose(); }
    }


    // 她的决策要 3~6 秒，而 AutoSlay 的"选项有没有变"只等 5 秒 —— 这里把这类短等待拉长
    static async Task UntilLong(Func<bool> cond, CancellationToken ct, TimeSpan timeout)
    {
        DateTime end = DateTime.UtcNow.Add(timeout);
        while (!cond())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("等待选项变化超时（已延长）");
            await Task.Delay(100, ct);
        }
    }

    static bool PrefixUntil(Func<bool> condition, CancellationToken ct, TimeSpan? timeout, Func<string> timeoutMessage, ref Task __result)
    {
        try
        {
            // 她的决策 3~10 秒，AutoSlay 很多等待只有 5~10 秒 —— 统一放宽到 45 秒
            if (!timeout.HasValue || timeout.Value.TotalSeconds > 12) return true;
            string msg = null;
            try { msg = timeoutMessage != null ? timeoutMessage() : null; } catch { }
            W("延长等待：" + msg + "（" + timeout.Value.TotalSeconds + "s -> 45s）");
            __result = UntilLong(condition, ct, TimeSpan.FromSeconds(45));
            return false;
        }
        catch { return true; }
    }

    static bool PrefixWithTimeout(Func<CancellationToken, Task> action, TimeSpan timeout, CancellationToken ct, ref Task __result)
    {
        if (timeout.TotalMinutes < 20) return true;   // 其它短超时保持原样
        W("接管整局超时：" + timeout.TotalMinutes + " 分钟 -> 3 小时");
        __result = LongRun(action, ct);
        return false;
    }

    // 当前角色（给插件选对应分层的分数）
    static string CharId()
    {
        try
        {
            object p = RunPlayer();
            object ch = Prop(p, "Character");
            string id = Convert.ToString(Prop(ch, "Id"));
            if (string.IsNullOrEmpty(id)) id = Convert.ToString(Prop(ch, "Title"));
            id = id.Replace("CHARACTER.", "").ToLowerInvariant();
            return id;
        }
        catch { return ""; }
    }

    // 卡组概况：给插件做"按当前卡组调整分数"用
    static string DeckSummary(object player)
    {
        try
        {
            object deck = Prop(player, "Deck");
            List<object> cards = L(Prop(deck, "Cards"));
            if (cards.Count == 0) return "";
            int total = cards.Count;
            int expensive = 0, energyCards = 0, exhaust = 0, draw = 0, block = 0;
            System.Collections.Generic.Dictionary<string, int> counts = new System.Collections.Generic.Dictionary<string, int>();
            for (int i = 0; i < cards.Count; i++)
            {
                object c = cards[i];
                string t = Loc2(Prop(c, "Description"));
                string title = S(Prop(c, "Title"));
                if (t.IndexOf("消耗") >= 0) exhaust++;
                if (t.IndexOf("抽") >= 0) draw++;
                if (t.IndexOf("格挡") >= 0) block++;
                if (t.IndexOf("能量") >= 0) energyCards++;
                int cost = CardCost(c);
                if (cost >= 2) expensive++;
                if (!string.IsNullOrEmpty(title))
                {
                    int cur;
                    counts.TryGetValue(title, out cur);
                    counts[title] = cur + 1;
                }
            }
            StringBuilder titles = new StringBuilder();
            bool firstPair = true;
            foreach (KeyValuePair<string, int> kv in counts)
            {
                if (!firstPair) titles.Append(',');
                firstPair = false;
                titles.Append('"').Append(J(kv.Key)).Append("\":").Append(kv.Value);
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"size\":").Append(total)
              .Append(",\"expensive\":").Append(expensive)
              .Append(",\"energy\":").Append(energyCards)
              .Append(",\"exhaust\":").Append(exhaust)
              .Append(",\"draw\":").Append(draw)
              .Append(",\"block\":").Append(block)
              .Append(",\"counts\":{").Append(titles.ToString()).Append("}}");
            return sb.ToString();
        }
        catch { return ""; }
    }




    static DeckInfo DeckInfoOf(object player)
    {
        DeckInfo d = new DeckInfo();
        try
        {
            object deck = Prop(player, "Deck");
            List<object> cards = L(Prop(deck, "Cards"));
            d.Size = cards.Count;
            for (int i = 0; i < cards.Count; i++)
            {
                object c = cards[i];
                string t = Loc2(Prop(c, "Description"));
                if (S(Prop(c, "Type")).IndexOf("Attack", StringComparison.OrdinalIgnoreCase) >= 0) d.Attacks++;
                if (t.IndexOf("消耗") >= 0) d.Exhaust++;
                if (t.IndexOf("抽") >= 0) d.Draw++;
                if (t.IndexOf("格挡") >= 0) d.Block++;
                if (t.IndexOf("能量") >= 0) d.Energy++;
                if (CardCost(c) >= 2) d.Expensive++;
                if (i < 60) d.Titles.Add(S(Prop(c, "Title")));
            }
        }
        catch { }
        return d;
    }


    static object OverlayStack()
    {
        try
        {
            Type t = _sts2.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack", false);
            if (t == null) return null;
            PropertyInfo p = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            return p == null ? null : p.GetValue(null, null);
        }
        catch { return null; }
    }

    static bool IsTypeOrBase(object o, string baseName)
    {
        if (o == null) return false;
        Type t = o.GetType();
        while (t != null) { if (t.Name == baseName) return true; t = t.BaseType; }
        return false;
    }

    static void CollectHolders(Node n, List<Node> acc, int depth)
    {
        if (n == null || depth > 12 || acc.Count > 20) return;
        if (IsTypeOrBase(n, "NCardHolder")) { acc.Add(n); return; }
        int c = 0;
        try { c = n.GetChildCount(); } catch { return; }
        for (int i = 0; i < c; i++)
        {
            Node ch = null;
            try { ch = n.GetChild(i); } catch { }
            CollectHolders(ch, acc, depth + 1);
        }
    }

    // 从卡片容器找到卡模型（holder -> NCard -> Model）
    static void CollectByTypeName(Node n, string typeName, List<Node> acc, int depth)
    {
        if (n == null || depth > 10 || acc.Count > 20) return;
        if (n.GetType().Name == typeName) { acc.Add(n); return; }
        int c = 0;
        try { c = n.GetChildCount(); } catch { return; }
        for (int i = 0; i < c; i++)
        {
            Node ch = null;
            try { ch = n.GetChild(i); } catch { }
            CollectByTypeName(ch, typeName, acc, depth + 1);
        }
    }

    static object CardOfHolder(Node holder)
    {
        try
        {
            object c = Prop(holder, "Card");
            if (c != null) return c;
            c = Prop(holder, "CardModel");
            if (c != null) return c;
            List<Node> ncs = new List<Node>();
            CollectByTypeName(holder, "NCard", ncs, 0);
            for (int i = 0; i < ncs.Count; i++)
            {
                object m = Prop(ncs[i], "Model");
                if (m == null) m = Prop(ncs[i], "Card");
                if (m != null) return m;
            }
        }
        catch { }
        return null;
    }

    // 给别的类用：这张牌升级是否"质变"（文本变化 / 费效跃升），且不是初始打击防御
    public static bool UpgradeWorthy(string title, out string note)
    {
        note = "";
        if (string.IsNullOrEmpty(title)) return false;
        if (title.IndexOf("打击") >= 0 || title.IndexOf("防御") >= 0) return false;
        CardInfo c = FindCardInfo(title, CharId());
        if (c == null) return false;
        if (!string.IsNullOrEmpty(c.DescUp) && c.DescUp != c.Desc) { note = "升级改文本"; return true; }
        if (c.Eff > 0 && c.EffUp > 0 && c.EffUp / c.Eff >= 1.5) { note = "费效" + c.Eff + "→" + c.EffUp; return true; }
        return false;
    }

    // 卡组里所有牌名
    public static List<string> DeckTitles()
    {
        List<string> titles = new List<string>();
        try
        {
            object deck = Prop(RunPlayer(), "Deck");
            foreach (object c in L(Prop(deck, "Cards")))
                titles.Add(S(Prop(c, "Title")));
        }
        catch { }
        return titles;
    }

    // 当前血量百分比
    public static int HpPctNow()
    {
        try
        {
            object cre = Prop(RunPlayer(), "Creature");
            int hp = I(cre, "CurrentHp"), max = I(cre, "MaxHp");
            return max <= 0 ? 100 : (int)(hp * 100.0 / max);
        }
        catch { return 100; }
    }

    // ---------- 卡牌奖励：让她选（替换 AutoSlay 的随机选） ----------
    static bool _skipRoomOnce;
    static int _autoPlayDbg;
    static volatile bool _runTurnActive;
    static bool _meteorProbed;
    static bool _intentProbed;

    // 继续存档后，当前房间其实已完成（奖励屏已处理、地图已开）→ 跳过这一次房间处理
    static bool PrefixCombatSkip(object __instance, ref Task __result)
    {
        if (!AgentMode()) return true;
        if (!_skipRoomOnce) return true;
        _skipRoomOnce = false;
        W("跳过已完成的战斗房（继续存档后）");
        __result = Task.CompletedTask;
        return false;
    }

    // 看门狗：永远不判定卡死（单次决策可能超过 30 秒）
    static bool PrefixWatchdogCheck()
    {
        return false;
    }

    static async Task RunCardRewardAsync(CancellationToken ct)
    {
        await Task.Delay(400, ct);
        List<Node> holders = new List<Node>();
        for (int attempt = 0; attempt < 15 && holders.Count == 0; attempt++)
        {
            RunOnMain(new Action(() =>
            {
                holders.Clear();
                object screen = null;
                object stack = OverlayStack();
                if (stack != null) { try { screen = stack.GetType().GetMethod("Peek").Invoke(stack, null); } catch { } }
                if (screen == null) return;
                CollectHolders(screen as Node, holders, 0);
            }));
            if (holders.Count == 0) await Task.Delay(200, ct);
        }
        if (holders.Count == 0) { W("奖励屏: 没有可选卡"); return; }

        string ask = null;
        RunOnMain(new Action(() =>
        {
            try
            {
                string chId = CharId();
                DeckInfo deckNow = DeckInfoOf(RunPlayer());
                int id = Interlocked.Increment(ref _seq);
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"id\":").Append(id).Append(",\"kind\":\"pick_cards\",\"min\":1,\"max\":1,\"reward\":true,\"char\":\"").Append(J(chId)).Append('"');
                sb.Append(",\"options\":[");
                for (int i = 0; i < holders.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    object card = CardOfHolder(holders[i]);
                    string ttl = S(Prop(card, "Title"));
                    sb.Append("{\"i\":").Append(i).Append(",\"title\":\"").Append(J(ttl)).Append('"');
                    CardInfo info = FindCardInfo(ttl, chId);
                    if (info != null)
                    {
                        if (!string.IsNullOrEmpty(info.Desc)) sb.Append(",\"desc\":\"").Append(J(info.Desc)).Append('"');
                        if (!string.IsNullOrEmpty(info.DescUp) && info.DescUp != info.Desc) sb.Append(",\"descUp\":\"").Append(J(info.DescUp)).Append('"');
                        sb.Append(",\"eff\":").Append(info.Eff.ToString("0.##"));
                        if (info.Aoe) sb.Append(",\"aoe\":true");
                        if (info.NeedsEnergy) sb.Append(",\"needsEnergy\":true");
                        int sc;
                        string key = info.Id + "|" + chId;
                        if (_kbScore.TryGetValue(key, out sc))
                        {
                            string notes;
                            int adj = AdjustScore(info, deckNow, sc, out notes);
                            sb.Append(",\"score\":").Append(adj);
                            string tier;
                            if (_kbTier.TryGetValue(key, out tier) && !string.IsNullOrEmpty(tier)) sb.Append(",\"tier\":\"").Append(tier).Append('"');
                            string kwNow;
                            if (_kbKw.TryGetValue(key, out kwNow) && !string.IsNullOrEmpty(kwNow)) sb.Append(",\"kw\":\"").Append(J(kwNow)).Append('"');
                            if (!string.IsNullOrEmpty(notes)) sb.Append(",\"adjust\":\"").Append(J(notes)).Append('"');
                        }
                    }
                    sb.Append('}');
                }
                sb.Append("]");
                string dsum = DeckSummary(RunPlayer());
                if (dsum.Length > 0) sb.Append(",\"deck\":").Append(dsum);
                sb.Append('}');
                ask = sb.ToString();
            }
            catch (Exception e) { W("奖励屏 payload 失败: " + e.Message); }
        }));

        if (ask != null) W("奖励局面: " + ask.Substring(0, Math.Min(320, ask.Length)));

        int pick = 0;
        if (ask != null)
        {
            try
            {
                string resp = Ask(ask, "reward");
                Match m = Regex.Match(resp ?? "", "\"picks\"\\s*:\\s*\\[([^\\]]*)\\]");
                if (m.Success)
                {
                    int v;
                    if (int.TryParse(m.Groups[1].Value.Split(',')[0].Trim(), out v) && v >= 0 && v < holders.Count) pick = v;
                }
            }
            catch (Exception e) { W("奖励屏决策失败，选第一张: " + e.Message); }
        }

        W("奖励卡 → 她选第 " + pick + " 张（共 " + holders.Count + " 张）");
        Node chosen = holders[pick];
        RunOnMain(new Action(() =>
        {
            try
            {
                MethodInfo fc = chosen.GetType().GetMethod("ForceClick", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fc != null) { _inFallback = true; fc.Invoke(chosen, fc.GetParameters().Length == 0 ? null : new object[] { false }); _inFallback = false; return; }
                GodotObject go = chosen as GodotObject;
                if (go != null)
                {
                    string sig = go.HasSignal("Pressed") ? "Pressed" : (go.HasSignal("pressed") ? "pressed" : null);
                    if (sig != null)
                    {
                        Godot.Node nt = go as Godot.Node;
                        go.EmitSignal(sig, nt);
                    }
                }
            }
            catch (Exception e) { W("点奖励卡失败: " + e.Message); }
        }));

        for (int round = 0; round < 3; round++)
        {
            bool closed = false;
            for (int i = 0; i < 15; i++)
            {
                string top = "";
                RunOnMain(new Action(() =>
                {
                    object stack = OverlayStack();
                    if (stack == null || I(stack, "ScreenCount") <= 0) return;
                    object ov = null;
                    try { ov = stack.GetType().GetMethod("Peek").Invoke(stack, null); } catch { }
                    top = ov == null ? "" : ov.GetType().Name;
                }));
                if (top != "NCardRewardSelectionScreen") { closed = true; break; }
                await Task.Delay(150, ct);
            }
            if (closed)
            {
                // 选完卡后，可能还挂着奖励列表屏 → 顺手关掉，避免主循环卡在等它
                for (int k = 0; k < 10; k++)
                {
                    string top2 = "";
                    RunOnMain(new Action(() =>
                    {
                        object stack = OverlayStack();
                        if (stack == null || I(stack, "ScreenCount") <= 0) return;
                        object ov = null;
                        try { ov = stack.GetType().GetMethod("Peek").Invoke(stack, null); } catch { }
                        top2 = ov == null ? "" : ov.GetType().Name;
                    }));
                    if (top2 != "NRewardsScreen") break;
                    BuildScreenHandlers();
                    object rh;
                    if (_screenH.TryGetValue("NRewardsScreen", out rh))
                    {
                        try
                        {
                            MethodInfo hm = rh.GetType().GetMethod("HandleAsync", BindingFlags.Public | BindingFlags.Instance);
                            Task ht = hm.Invoke(rh, new object[] { RunnerRng(), ct }) as Task;
                            if (ht != null) await ht;
                        }
                        catch { }
                    }
                    await Task.Delay(300, ct);
                }
                W("奖励屏已关闭");
                return;
            }
            W("奖励屏未关，重点一次（第 " + (round + 1) + " 次）");
            RunOnMain(new Action(() =>
            {
                try
                {
                    GodotObject go = chosen as GodotObject;
                    if (go != null)
                    {
                        string sig = go.HasSignal("Pressed") ? "Pressed" : (go.HasSignal("pressed") ? "pressed" : null);
                        if (sig != null) go.EmitSignal(sig, (Godot.Node)go);
                    }
                }
                catch { }
            }));
            await Task.Delay(800, ct);
        }
        W("奖励屏未关闭");
    }

    static bool PrefixCardRewardHandle(object __instance, ref Task __result)
    {
        if (!AgentMode()) return true;
        if (_inFallback) return true;
        __result = RunCardRewardAsync(CancellationToken.None);
        return false;
    }


    // ---------- Agent 模式开关（一次性 flag，由 QQ 插件写入） ----------
    static bool _agentMode;
    static bool _agentModeChecked;
    static readonly string AutoplayFlag = Paths.Data + @"\autoplay.flag";

    static bool AgentMode()
    {
        if (_agentModeChecked) return _agentMode;
        _agentModeChecked = true;
        try
        {
            if (!File.Exists(AutoplayFlag)) { W("手动模式：未检测到 flag，不接管"); return _agentMode = false; }
            DateTime t = File.GetLastWriteTime(AutoplayFlag);
            if ((DateTime.Now - t).TotalMinutes > 5)
            {
                W("flag 已过期（" + t.ToString("HH:mm") + "），按手动模式");
                try { File.Delete(AutoplayFlag); } catch { }
                return _agentMode = false;
            }
            try { File.Delete(AutoplayFlag); } catch { }
            try { File.WriteAllText(Paths.Data + @"\autoplay.active", DateTime.Now.ToString("o")); } catch { }
            W("Agent 模式：flag 已消费，开始接管");
            return _agentMode = true;
        }
        catch (Exception e) { W("读 flag 失败: " + e.Message); return _agentMode = false; }
    }

    // ---------- 开局：按角色文件或问她选角色 ----------
    static readonly string NextCharFile = Paths.Data + @"\next-character.txt";

    // ---------- 覆盖屏排空（继续存档后可能挂着奖励屏） ----------
    static readonly Dictionary<string, object> _screenH = new Dictionary<string, object>();
    static bool _screenBuilt;

    static object _rngObj2;
    static object RunnerRng()
    {
        if (_rngObj2 != null) return _rngObj2;
        try
        {
            Type rt = _sts2.GetType("MegaCrit.Sts2.Core.Random.Rng", false);
            Type sh = _sts2.GetType("MegaCrit.Sts2.Core.Helpers.StringHelper", false);
            object seedArg = 20251025;
            MethodInfo gh = sh == null ? null : sh.GetMethod("GetDeterministicHashCode", BindingFlags.Public | BindingFlags.Static);
            if (gh != null) seedArg = gh.Invoke(null, new object[] { Seed });
            _rngObj2 = Activator.CreateInstance(rt, new object[] { seedArg });
        }
        catch (Exception e) { W("Rng 创建失败: " + e.Message); }
        return _rngObj2;
    }

    static void BuildScreenHandlers()
    {
        if (_screenBuilt) return;
        _screenBuilt = true;
        string[,] map = new string[,] {
            {"NRewardsScreen", "RewardsScreenHandler"},
            {"NCardRewardSelectionScreen", "CardRewardScreenHandler"},
            {"NDeckUpgradeSelectScreen", "DeckUpgradeScreenHandler"},
            {"NDeckTransformSelectScreen", "DeckTransformScreenHandler"},
            {"NDeckEnchantSelectScreen", "DeckEnchantScreenHandler"},
            {"NDeckCardSelectScreen", "DeckCardSelectScreenHandler"},
            {"NSimpleCardSelectScreen", "SimpleCardSelectScreenHandler"},
            {"NChooseACardSelectionScreen", "ChooseACardScreenHandler"},
            {"NChooseABundleSelectionScreen", "ChooseABundleScreenHandler"},
            {"NChooseARelicSelection", "ChooseARelicScreenHandler"},
            {"NCrystalSphereScreen", "CrystalSphereScreenHandler"}
        };
        for (int i = 0; i < map.GetLength(0); i++)
        {
            try
            {
                Type ht = _sts2.GetType("MegaCrit.Sts2.Core.AutoSlay.Handlers.Screens." + map[i, 1], false);
                if (ht == null)
                {
                    foreach (Type t in _sts2.GetTypes()) if (t != null && t.Name == map[i, 1]) { ht = t; break; }
                }
                if (ht != null) _screenH[map[i, 0]] = Activator.CreateInstance(ht);
            }
            catch { }
        }
        W("屏幕处理器: " + _screenH.Count + " 个");
    }

    static async Task DrainOverlaysAsync(CancellationToken ct)
    {
        BuildScreenHandlers();
        for (int guard = 0; guard < 40; guard++)
        {
            int cnt = 0; string top = "";
            RunOnMain(new Action(() =>
            {
                object stack = OverlayStack();
                if (stack == null) return;
                cnt = I(stack, "ScreenCount");
                if (cnt <= 0) return;
                object ov = null;
                try { ov = stack.GetType().GetMethod("Peek").Invoke(stack, null); } catch { }
                top = ov == null ? "" : ov.GetType().Name;
            }));
            if (cnt <= 0) return;

            if (top == "NCardRewardSelectionScreen") { await RunCardRewardAsync(ct); await Task.Delay(200, ct); continue; }

            object h;
            if (!_screenH.TryGetValue(top, out h)) { W("未接管屏幕: " + top); return; }
            try
            {
                MethodInfo m = h.GetType().GetMethod("HandleAsync", BindingFlags.Public | BindingFlags.Instance);
                object r = m.Invoke(h, new object[] { RunnerRng(), ct });
                Task t = r as Task;
                if (t != null) await t;
            }
            catch (Exception e)
            {
                Exception inner = e.InnerException != null ? e.InnerException : e;
                W("屏幕处理异常(" + top + "): " + inner.Message);
                return;
            }
            await Task.Delay(200, ct);
        }
    }

    // 继续存档后：把界面状态打出来，便于判断该点什么
    static bool MapIsOpen()
    {
        bool open = false;
        RunOnMain(new Action(() =>
        {
            try
            {
                Type mt = _sts2.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen", false);
                PropertyInfo ip = mt.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                object map = ip == null ? null : ip.GetValue(null, null);
                if (map == null) return;
                object o = Prop(map, "IsOpen");
                CanvasItem ci = map as CanvasItem;
                open = (o is bool && (bool)o) || (ci != null && ci.IsVisibleInTree());
            }
            catch { }
        }));
        return open;
    }

    static void LogUiState(string tag)
    {
        RunOnMain(new Action(() =>
        {
            try
            {
                object stack = OverlayStack();
                int cnt = stack == null ? -1 : I(stack, "ScreenCount");
                string top = "";
                if (stack != null && cnt > 0)
                {
                    object ov = null;
                    try { ov = stack.GetType().GetMethod("Peek").Invoke(stack, null); } catch { }
                    top = ov == null ? "" : ov.GetType().Name;
                }
                SceneTree tree = Engine.GetMainLoop() as SceneTree;
                bool pause = false;
                if (tree != null)
                {
                    Node p = FindNodeOfType(tree.Root, "NPauseMenu");
                    CanvasItem ci = p as CanvasItem;
                    pause = p != null && (ci == null || ci.IsVisibleInTree());
                }
                W("界面(" + tag + "): overlays=" + cnt + " top=" + top + " mapOpen=" + MapIsOpen() + " pause=" + pause);
            }
            catch (Exception e) { W("界面诊断失败: " + e.Message); }
        }));
    }

    static object RunStateNow()
    {
        try
        {
            Type t = _sts2.GetType("MegaCrit.Sts2.Core.Runs.RunManager", false);
            object rm = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
            if (rm == null) return null;
            MethodInfo m = rm.GetType().GetMethod("DebugOnlyGetState", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (m != null) return m.Invoke(rm, null);
            return Prop(rm, "State") ?? Field(rm, "<State>k__BackingField");
        }
        catch { return null; }
    }

    static int FloorOf(object runState)
    {
        try { return I(runState, "TotalFloor"); } catch { return 0; }
    }

    static string RoomTypeText(object runState)
    {
        try { return Convert.ToString(Prop(Prop(runState, "CurrentRoom"), "RoomType")); }
        catch { return ""; }
    }

    static Node NodeAt(string path)
    {
        try
        {
            SceneTree tree = Engine.GetMainLoop() as SceneTree;
            if (tree == null) return null;
            return tree.Root.GetNodeOrNull(path);
        }
        catch { return null; }
    }

    static string DesiredCharacter()
    {
        try
        {
            if (!File.Exists(NextCharFile)) return "auto";
            string t = File.ReadAllText(NextCharFile).Trim().ToLowerInvariant();
            return string.IsNullOrEmpty(t) ? "auto" : t;
        }
        catch { return "auto"; }
    }

    static string CharIdOf(object btn)
    {
        try
        {
            object ch = Prop(btn, "Character");
            string id = Convert.ToString(Prop(ch, "Id"));
            if (string.IsNullOrEmpty(id)) id = Convert.ToString(Prop(ch, "Title"));
            id = id.Replace("CHARACTER.", "").ToLowerInvariant();
            return id;
        }
        catch { return ""; }
    }

    // 找"继续游戏"按钮（名字含 Continue/Resume）
    static Node FindContinueButton(bool logList)
    {
        Node found = null;
        try
        {
            Node box = NodeAt("/root/Game/RootSceneContainer/MainMenu/MainMenuTextButtons");
            if (box == null) return null;
            int c = box.GetChildCount();
            StringBuilder names = new StringBuilder();
            for (int i = 0; i < c; i++)
            {
                Node ch = null;
                try { ch = box.GetChild(i); } catch { }
                if (ch == null) continue;
                string n = ch.GetType().Name;
                if (logList) names.Append(n).Append(' ');
                CanvasItem ci = ch as CanvasItem;
                bool vis = ci == null || ci.IsVisibleInTree();
                if (!vis) continue;
                if (n.IndexOf("Continue", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Resume", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = ch;
                    break;
                }
            }
            if (logList) W("主菜单按钮: " + names);
        }
        catch { }
        return found;
    }

    static async Task OurMainMenuAsync(CancellationToken ct)
    {
        W("开局：等主菜单");
        Node mainMenu = null;
        for (int i = 0; i < 150; i++)
        {
            RunOnMain(new Action(() => { mainMenu = NodeAt("/root/Game/RootSceneContainer/MainMenu"); }));
            if (mainMenu != null) break;
            await Task.Delay(200, ct);
        }
        if (mainMenu == null) { W("开局失败：没有主菜单"); return; }

        // 方案 C：有存档时
        //   auto  → 点"继续游戏"，接着玩
        //   指定角色 → 放弃旧局，按指定角色开新局
        string want0 = DesiredCharacter();
        bool hasRun = false;
        RunOnMain(new Action(() =>
        {
            Node b = NodeAt("/root/Game/RootSceneContainer/MainMenu/MainMenuTextButtons/AbandonRunButton");
            CanvasItem ci = b as CanvasItem;
            hasRun = b != null && (ci == null || ci.IsVisibleInTree());
        }));

        if (hasRun)
        {
            if (want0 == "auto")
            {
                bool ok = false;
                RunOnMain(new Action(() =>
                {
                    Node cont = FindContinueButton(true);
                    if (cont != null) { ForceClickNode(cont); ok = true; }
                }));
                if (ok)
                {
                    W("开局：有存档 → 继续游戏（不放弃）");
                    // 等存档真正加载完（房间类型分配好）再交给主循环
                    for (int w = 0; w < 300; w++)
                    {
                        string rtNow = "";
                        RunOnMain(new Action(() =>
                        {
                            object rs = RunStateNow();
                            if (rs != null) rtNow = RoomTypeText(rs);
                        }));
                        if (!string.IsNullOrEmpty(rtNow) && rtNow != "Unassigned")
                        {
                            W("开局：存档已加载（" + rtNow + "）");
                            await Task.Delay(1500, ct);
                            LogUiState("存档已加载");
                            bool hadOverlay = false;
                            RunOnMain(new Action(() =>
                            {
                                try
                                {
                                    object st3 = OverlayStack();
                                    hadOverlay = st3 != null && I(st3, "ScreenCount") > 0;
                                }
                                catch { }
                            }));
                            await DrainOverlaysAsync(ct);
                            LogUiState("排空后");
                            bool mapOpenNow = MapIsOpen();
                            if (hadOverlay || mapOpenNow)
                            {
                                _skipRoomOnce = true;    // 存档时挂着未处理界面 → 当前房间已算完成
                                W("存档时有界面残留，跳过该房间");
                            }
                            else
                            {
                                W("存档是干净的战斗房，正常打（不跳过）");
                            }
                            return;
                        }
                        await Task.Delay(300, ct);
                    }
                    W("开局：等存档加载超时，仍交给主循环");
                    return;
                }
                W("开局：找不到继续按钮，改为放弃旧局");
            }
            W("开局：放弃旧局（指定角色 " + want0 + "）");
            RunOnMain(new Action(() =>
            {
                Node b = NodeAt("/root/Game/RootSceneContainer/MainMenu/MainMenuTextButtons/AbandonRunButton");
                if (b != null) ForceClickNode(b);
            }));
            await Task.Delay(700, ct);
            RunOnMain(new Action(() =>
            {
                try
                {
                    Type mc = _sts2.GetType("MegaCrit.Sts2.Core.Nodes.CommonUi.NModalContainer", false);
                    if (mc == null) mc = _sts2.GetType("MegaCrit.Sts2.Core.Nodes.Modals.NModalContainer", false);
                    object inst = mc == null ? null : mc.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                    object modal = inst == null ? null : Prop(inst, "OpenModal");
                    Node mn = modal as Node;
                    if (mn != null)
                    {
                        Node yes = mn.GetNodeOrNull("VerticalPopup/YesButton");
                        if (yes != null) { ForceClickNode(yes); W("已确认放弃"); return; }
                    }
                }
                catch (Exception e) { W("点放弃确认失败: " + e.Message); }
                Node alt = FindNodeOfType(NodeAt("/root/Game/RootSceneContainer/MainMenu"), "NYesButton");
                if (alt != null) ForceClickNode(alt);
            }));
            await Task.Delay(1200, ct);
        }

        // 单人
        RunOnMain(new Action(() =>
        {
            Node sp = NodeAt("/root/Game/RootSceneContainer/MainMenu/MainMenuTextButtons/SingleplayerButton");
            if (sp != null) ForceClickNode(sp);
        }));
        await Task.Delay(800, ct);

        // 标准模式（若出现）
        for (int i = 0; i < 20; i++)
        {
            bool clicked = false;
            RunOnMain(new Action(() =>
            {
                Node std = NodeAt("/root/Game/RootSceneContainer/MainMenu/Submenus/SingleplayerSubmenu/StandardButton");
                CanvasItem ci = std as CanvasItem;
                if (std != null && (ci == null || ci.IsVisibleInTree())) { ForceClickNode(std); clicked = true; }
            }));
            if (clicked) { W("开局：标准模式"); break; }
            bool charScreen = false;
            RunOnMain(new Action(() =>
            {
                Node cs = NodeAt("/root/Game/RootSceneContainer/MainMenu/Submenus/CharacterSelectScreen");
                CanvasItem ci = cs as CanvasItem;
                charScreen = cs != null && (ci == null || ci.IsVisibleInTree());
            }));
            if (charScreen) break;
            await Task.Delay(300, ct);
        }
        await Task.Delay(600, ct);

        // 角色选择
        List<Node> btns = new List<Node>();
        List<string> names = new List<string>();
        List<string> ids = new List<string>();
        RunOnMain(new Action(() =>
        {
            Node container = NodeAt("/root/Game/RootSceneContainer/MainMenu/Submenus/CharacterSelectScreen/CharSelectButtons/ButtonContainer");
            if (container == null) return;
            int c = 0;
            try { c = container.GetChildCount(); } catch { }
            for (int i = 0; i < c; i++)
            {
                Node ch = null;
                try { ch = container.GetChild(i); } catch { }
                if (ch == null || ch.GetType().Name != "NCharacterSelectButton") continue;
                MethodInfo un = ch.GetType().GetMethod("UnlockIfPossible", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (un != null) { try { un.Invoke(ch, null); } catch { } }
                object locked = Prop(ch, "IsLocked");
                if (locked is bool && (bool)locked) continue;
                object chm = Prop(ch, "Character");
                string nm = Loc2(Prop(chm, "Title"));
                if (string.IsNullOrEmpty(nm)) nm = CharIdOf(ch);
                btns.Add(ch); names.Add(nm); ids.Add(CharIdOf(ch));
            }
        }));
        if (btns.Count == 0) { W("开局失败：没有可选角色"); return; }

        string want = DesiredCharacter();
        int pick = -1;
        if (want != "auto")
        {
            for (int i = 0; i < ids.Count; i++) if (ids[i] == want) { pick = i; break; }
            if (pick < 0) W("要求的角色 " + want + " 不可用，改为问她");
        }

        if (pick < 0)
        {
            // 问她
            int idSeq = Interlocked.Increment(ref _seq);
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"id\":").Append(idSeq).Append(",\"kind\":\"pick_character\",\"options\":[");
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"i\":").Append(i).Append(",\"name\":\"").Append(J(names[i])).Append('"')
                  .Append(",\"id\":\"").Append(J(ids[i])).Append("\"}");
            }
            sb.Append("]}");
            try
            {
                string resp = Ask(sb.ToString(), "pick_character");
                Match m = Regex.Match(resp ?? "", "\"index\"\\s*:\\s*(-?\\d+)");
                if (m.Success) { int v = int.Parse(m.Groups[1].Value); if (v >= 0 && v < btns.Count) pick = v; }
            }
            catch (Exception e) { W("选角色决策失败: " + e.Message); }
            if (pick < 0) pick = 0;
            W("开局：她选了 " + names[pick]);
        }
        else W("开局：指定角色 " + want);

        Node chosen = btns[pick];
        RunOnMain(new Action(() =>
        {
            MethodInfo sel = chosen.GetType().GetMethod("Select", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (sel != null) { try { sel.Invoke(chosen, null); } catch (Exception e) { W("角色 Select 失败: " + e.Message); } }
        }));
        await Task.Delay(300, ct);

        // 种子 + 确认
        RunOnMain(new Action(() =>
        {
            try
            {
                Type gt = _sts2.GetType("MegaCrit.Sts2.Core.Nodes.NGame");
                object game = gt.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                PropertyInfo dp = gt.GetProperty("DebugSeedOverride", BindingFlags.Public | BindingFlags.Instance);
                if (dp != null && dp.CanWrite) dp.SetValue(game, Seed, null);
            }
            catch { }
            Node confirm = NodeAt("/root/Game/RootSceneContainer/MainMenu/Submenus/CharacterSelectScreen/ConfirmButton");
            if (confirm != null) ForceClickNode(confirm);
        }));
        W("开局：已确认");
    }

    // 一局结束：通知插件生成记忆
    // 结算截图：抓游戏画面存成 png，返回路径（失败返回空）
    static string CaptureGameShot()
    {
        string path = Paths.Shots + @"\sts2-shot-" + DateTime.Now.ToString("HHmmss") + ".png";
        bool ok = false;
        RunOnMain(new Action(() =>
        {
            try
            {
                SceneTree tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null || tree.Root == null) return;
                Viewport vp = tree.Root;
                Texture2D tex = vp.GetTexture();
                if (tex == null) return;
                Image img = tex.GetImage();
                if (img == null) return;
                img.SavePng(path);
                ok = true;
            }
            catch (Exception e) { W("截图失败: " + e.Message); }
        }));
        if (!ok) return "";
        try { if (!File.Exists(path)) return ""; } catch { return ""; }
        return path;
    }

    static void PostRunEnd(int floor, string result, string charId)
    {
        try
        {
            string shotPath = CaptureGameShot();
        if (shotPath.Length > 0) W("结算截图: " + shotPath);
        string body = "{\"floor\":" + floor + ",\"result\":\"" + result + "\",\"char\":\"" + charId + "\""
            + (shotPath.Length > 0 ? ",\"shot\":\"" + shotPath.Replace("\\", "\\\\") + "\"" : "")
            + "}";
            StringContent c = new StringContent(body, Encoding.UTF8, "application/json");
            Http.PostAsync(Base + "/runend", c).ContinueWith(delegate (Task<HttpResponseMessage> t)
            {
                W("记忆请求已发送: " + (t.IsFaulted ? "失败" : "成功"));
            });
        }
        catch (Exception e) { W("PostRunEnd 失败: " + e.Message); }
    }

    static void TaskCompletes()
    {
        try { File.Delete(Paths.Data + @"\autoplay.active"); } catch { }
        W("对局结束，已清除 Agent 活动凭证");
    }

    static bool PrefixMainMenu(ref Task __result)
    {
        if (!AgentMode()) return true;
        if (_inFallback) return true;
        __result = OurMainMenuAsync(CancellationToken.None);
        return false;
    }

    // ---------- 作弊开关（插件 UI 控制，默认关闭=不作弊） ----------
    static bool? _useCheats;
    static bool UseCheats()
    {
        if (_useCheats.HasValue) return _useCheats.Value;
        bool val = false;
        try
        {
            using (System.Net.Http.HttpClient c = new System.Net.Http.HttpClient())
            {
                c.Timeout = TimeSpan.FromSeconds(2);
                string r = c.GetStringAsync(Base + "/options").Result;
                val = r != null && r.IndexOf("\"useCheats\":true", StringComparison.Ordinal) >= 0;
            }
        }
        catch { val = false; }
        _useCheats = val;
        W("作弊开关: " + (val ? "开启" : "关闭（真实强度）"));
        return val;
    }

    // 把 AutoSlay 塞进来的作弊力量清掉（护甲/回复 999、力量 200、事件房 100）
    static void StripCheats(object player)
    {
        if (UseCheats()) return;
        RunOnMain(new Action(() =>
        {
            try
            {
                object cre = Prop(player, "Creature");
                object raw = Field(cre, "_powers");
                IEnumerable seq = raw as IEnumerable;
                if (seq == null) return;
                List<object> targets = new List<object>();
                foreach (object pw in seq)
                {
                    if (pw == null) continue;
                    string tn = pw.GetType().Name;
                    if (tn.IndexOf("Plating", StringComparison.OrdinalIgnoreCase) < 0 &&
                        tn.IndexOf("Regen", StringComparison.OrdinalIgnoreCase) < 0 &&
                        tn.IndexOf("Strength", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (I(pw, "Amount") >= 100) targets.Add(pw);
                }
                if (targets.Count == 0) return;

                // 先把数值清零（保险）
                for (int i = 0; i < targets.Count; i++)
                {
                    SetProp(targets[i], "Amount", 0);
                    SetProp(targets[i], "AmountOnTurnStart", 0);
                }

                // 再调用游戏自身的 PowerCmd.Remove 彻底移除
                Type pc = _sts2.GetType("MegaCrit.Sts2.Core.Commands.PowerCmd", false);
                MethodInfo rm = null;
                if (pc != null)
                {
                    foreach (MethodInfo m in pc.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name != "Remove") continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType.Name == "PowerModel") { rm = m; break; }
                    }
                }
                for (int i = 0; i < targets.Count; i++)
                {
                    string nm = targets[i].GetType().Name;
                    bool done = false;
                    if (rm != null)
                    {
                        try { rm.Invoke(null, new object[] { targets[i] }); done = true; }
                        catch (Exception ex) { Exception ie = ex.InnerException != null ? ex.InnerException : ex; W("移除失败(" + nm + "): " + ie.Message); }
                    }
                    W("作弊力量处理: " + nm + (done ? " 已移除" : " 已清零"));
                }
            }
            catch (Exception e) { W("清除作弊失败: " + e.Message); }
        }));
    }

    // 每场战斗只处理一次
    static int _lastCombatId = -1;

    static void SetProp(object o, string name, object value)
    {
        try
        {
            PropertyInfo pi = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (pi != null && pi.CanWrite) { pi.SetValue(o, value, null); return; }
            FieldInfo fi = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi != null) fi.SetValue(o, value);
        }
        catch { }
    }

    // ---------- 在"执行前"拦掉 AutoSlay 的作弊加力量 ----------
    static int _cheatDbg;
    static bool PrefixCheatPower(object[] __args)
    {
        if (UseCheats()) return true;
        try
        {
            if (__args != null)
            {
                StringBuilder dbg = new StringBuilder();
                for (int i = 0; i < __args.Length && i < 6; i++)
                {
                    object v = __args[i];
                    dbg.Append(i).Append('=').Append(v == null ? "null" : v.GetType().Name + ":" + Convert.ToString(v)).Append("  ");
                }
                if (_cheatDbg < 12) { _cheatDbg++; W("Apply参数[" + __args.Length + "]: " + dbg); }
                for (int i = 0; i < __args.Length; i++)
                {
                    object v = __args[i];
                    if (v is decimal && (decimal)v >= 100m)
                    {
                        W("已拦截作弊加力量: " + v);
                        return false;
                    }
                }
            }
        }
        catch { }
        return true;
    }


    // ---------- 拦 PowerModel.Amount 的写入：玩家身上 >=100 的一律压成 0 ----------
    static int _cheatSetDbg;
    static bool PrefixAmountSet(object __instance, object[] __args)
    {
        if (UseCheats()) return true;
        try
        {
            if (__args == null || __args.Length < 1 || __instance == null) return true;
            int v = Convert.ToInt32(__args[0]);
            if (v < 100) return true;

            // 只处理"玩家身上的力量"
            bool isPlayer = false;
            try
            {
                object owner = Prop(__instance, "Owner") ?? Field(__instance, "<Owner>k__BackingField");
                object ply = owner == null ? null : Prop(owner, "Player");
                isPlayer = ply != null;
            }
            catch { }
            if (!isPlayer) return true;

            __args[0] = 0;
            if (_cheatSetDbg < 10)
            {
                _cheatSetDbg++;
                W("已拦截作弊写入: " + __instance.GetType().Name + " " + v + " → 0");
            }
        }
        catch { }
        return true;
    }



    static IEnumerable<CodeInstruction> TranspileCombatCheats(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> list = new List<CodeInstruction>(instructions);
        for (int i = 0; i < list.Count; i++)
        {
            CodeInstruction ci = list[i];
            try
            {
                string n = ci.opcode.Name;   // 用名字比较，避免 OpCode 类型歧义
                if (n == "call" || n == "callvirt")
                {
                    MethodInfo callee = ci.operand as MethodInfo;
                    if (callee != null && callee.Name == "AutoPlay" && callee.DeclaringType.Name == "CardCmd")
                    {
                        MethodInfo hook = typeof(AgentSeatEntry).GetMethod("HookAutoPlay", BindingFlags.Public | BindingFlags.Static);
                        if (hook != null) { list[i] = new CodeInstruction(ci.opcode, hook); W("已把 AutoPlay 调用点改写到 HookAutoPlay"); }
                    }
                }
                // 只替换作弊常量 999 / 200；100 是回合上限和等待时长，绝对不能动
                if (n == "ldc.i4" && ci.operand is int)
                {
                    int v = (int)ci.operand;
                    if (v == 999 || v == 200) list[i] = new CodeInstruction(ci.opcode, 0);
                }
                else if (n == "ldc.i4.s" && ci.operand is sbyte)
                {
                    sbyte v2 = (sbyte)ci.operand;
                    if (v2 == 200 || v2 == 999) list[i] = new CodeInstruction(ci.opcode, (sbyte)0);
                }
                else if ((n == "ldc.r8" || n == "ldc.r4") && ci.operand is double)
                {
                    double d = (double)ci.operand;
                    if (Math.Abs(d - 999.0) < 0.01 || Math.Abs(d - 200.0) < 0.01)
                        list[i] = new CodeInstruction(ci.opcode, 0.0);
                }
            }
            catch { }
        }
        return list;
    }

    // 事件战斗：整个作弊流程（+100 三连 + 直接杀敌）换成"等战斗结束"
    static async Task WaitEventCombatEndAsync()
    {
        W("事件战斗：不加载作弊，正常打");
        for (int i = 0; i < 2400; i++)
        {
            bool inProgress = false;
            RunOnMain(new Action(() =>
            {
                try
                {
                    Type cm = _sts2.GetType("MegaCrit.Sts2.Core.Combat.CombatManager", false);
                    object inst = cm == null ? null : cm.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                    object v = inst == null ? null : Prop(inst, "IsInProgress");
                    inProgress = v is bool && (bool)v;
                }
                catch { }
            }));
            if (!inProgress) return;
            await Task.Delay(500);
        }
        W("事件战斗等待超时（20 分钟）");
    }

    static bool PrefixEventCombat(ref Task __result)
    {
        if (UseCheats()) return true;
        __result = WaitEventCombatEndAsync();
        return false;
    }

    static void DisableCheats(Assembly a)
    {
        if (UseCheats()) { W("作弊开关开启，保留原版作弊"); return; }
        try
        {
            Type ch = a.GetType("MegaCrit.Sts2.Core.AutoSlay.Handlers.Rooms.CombatRoomHandler", false);
            MethodInfo ha = null;
            if (ch != null)
            {
                foreach (Type nt in ch.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (nt.Name.IndexOf("HandleAsync", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    MethodInfo mn = nt.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (mn != null) { ha = mn; W("找到状态机: " + nt.Name + ".MoveNext"); break; }
                }
            }
            if (ha != null)
            {
                _harmony.Patch(ha, null, null,
                    new HarmonyMethod(typeof(AgentSeatEntry).GetMethod("TranspileCombatCheats", BindingFlags.Static | BindingFlags.NonPublic)));
                W("战斗房作弊常量已清零（在 MoveNext 上，999/200 → 0）");
            }

            Type eh = a.GetType("MegaCrit.Sts2.Core.AutoSlay.Handlers.Rooms.EventRoomHandler", false);
            MethodInfo he = eh == null ? null : eh.GetMethod("HandleEventCombat", BindingFlags.NonPublic | BindingFlags.Instance);
            if (he != null)
            {
                _harmony.Patch(he,
                    new HarmonyMethod(typeof(AgentSeatEntry).GetMethod("PrefixEventCombat", BindingFlags.Static | BindingFlags.NonPublic)),
                    null, null);
                W("事件战斗作弊已替换（+100 与直接杀敌都已关闭）");
            }
        }
        catch (Exception e) { W("关作弊失败: " + e.Message); }
    }

    // 选项节点探针：打印每个可点选项的所有布尔成员（判断"禁用"到底存在哪）
    static string _optProbeSig = "";
    static void TickOptionProbe()
    {
        try
        {
            List<Node> hits = new List<Node>();
            RunOnMain(new Action(() =>
            {
                try
                {
                    SceneTree tree = Engine.GetMainLoop() as SceneTree;
                    if (tree != null)
                    {
                        CollectByTypeName(tree.Root, "NEventOptionButton", hits, 0);
                        if (hits.Count == 0) CollectByTypeName(tree.Root, "NRestSiteButton", hits, 0);
                    }
                }
                catch { }
            }));
            if (hits.Count == 0) return;

            StringBuilder sig = new StringBuilder();
            for (int i = 0; i < hits.Count; i++) sig.Append(hits[i].GetType().Name).Append('|');
            if (sig.ToString() == _optProbeSig) return;
            _optProbeSig = sig.ToString();

            for (int i = 0; i < hits.Count; i++)
            {
                Node n = hits[i];
                StringBuilder sb = new StringBuilder();
                Type t = n.GetType();
                foreach (FieldInfo fi in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (fi.FieldType != typeof(bool)) continue;
                    object v = null;
                    try { v = fi.GetValue(n); } catch { }
                    sb.Append(fi.Name).Append('=').Append(Convert.ToString(v)).Append("  ");
                }
                foreach (PropertyInfo pi in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (pi.PropertyType != typeof(bool) || !pi.CanRead) continue;
                    object v = null;
                    try { v = pi.GetValue(n, null); } catch { continue; }
                    sb.Append(pi.Name).Append('=').Append(Convert.ToString(v)).Append("  ");
                }
                CanvasItem ci = n as CanvasItem;
                string mod = "?";
                try { if (ci != null) { Color c = ci.Modulate; mod = c.ToHtml(); } } catch { }
                W("选项探针[" + i + "] " + t.Name + " modulate=" + mod + " | " + sb);
            }
        }
        catch (Exception e) { W("选项探针失败: " + e.Message); }
    }

    // 通用探针：列出当前所有可见的"屏幕/选牌"类节点类型
    static string _screenSig = "";
    static void TickScreenProbe()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            RunOnMain(new Action(() =>
            {
                try
                {
                    SceneTree tree = Engine.GetMainLoop() as SceneTree;
                    if (tree == null) return;
                    CollectVisibleScreenTypes(tree.Root, sb, 0);
                }
                catch { }
            }));
            string now = sb.ToString();
            if (now == _screenSig) return;
            _screenSig = now;
            W("可见屏幕: " + (now.Length == 0 ? "(无)" : now));
        }
        catch { }
    }

    static void CollectVisibleScreenTypes(Node n, StringBuilder acc, int depth)
    {
        if (n == null || depth > 16 || acc.Length > 600) return;
        try
        {
            string tn = n.GetType().Name;
            if (tn.IndexOf("Screen", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tn.IndexOf("Selection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tn.IndexOf("Bundle", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                CanvasItem ci = n as CanvasItem;
                if (ci == null || ci.IsVisibleInTree())
                {
                    if (acc.ToString().IndexOf(tn, StringComparison.Ordinal) < 0)
                        acc.Append(tn).Append(' ');
                }
            }
            int c = 0;
            try { c = n.GetChildCount(); } catch { return; }
            for (int i = 0; i < c; i++)
            {
                Node ch = null;
                try { ch = n.GetChild(i); } catch { }
                CollectVisibleScreenTypes(ch, acc, depth + 1);
            }
        }
        catch { }
    }

    // 卡包界面探针：把每个包里的牌打出来（手动模式也会跑）
    static string _bundleSig = "";
    static void TickBundleProbe()
    {
        try
        {
            object scr = null;
            RunOnMain(new Action(() =>
            {
                try
                {
                    SceneTree tree = Engine.GetMainLoop() as SceneTree;
                    if (tree == null) return;
                    List<Node> hits = new List<Node>();
                    CollectByTypeName(tree.Root, "NChooseABundleSelectionScreen", hits, 0);
                    for (int i = 0; i < hits.Count; i++)
                    {
                        CanvasItem ci = hits[i] as CanvasItem;
                        if (ci != null && ci.IsVisibleInTree()) { scr = hits[i]; break; }
                    }
                }
                catch { }
            }));
            if (scr == null) { _bundleSig = ""; return; }

            StringBuilder sb = new StringBuilder();
            RunOnMain(new Action(() =>
            {
                try
                {
                    IEnumerable bundles = Field(scr, "_bundles") as IEnumerable;
                    if (bundles == null) { sb.Append("(读不到 _bundles)"); return; }
                    int bi = 0;
                    foreach (object bundle in bundles)
                    {
                        sb.Append("包").Append(bi).Append(": ");
                        IEnumerable cards = bundle as IEnumerable;
                        if (cards != null)
                        {
                            int ci2 = 0;
                            foreach (object c in cards)
                            {
                                if (ci2 > 0) sb.Append(" + ");
                                sb.Append(S(Prop(c, "Title")));
                                ci2++;
                            }
                        }
                        sb.Append("  |  ");
                        bi++;
                    }
                }
                catch (Exception e) { sb.Append("异常: " + e.Message); }
            }));

            string now = sb.ToString();
            if (now == _bundleSig) return;
            _bundleSig = now;
            W("卡包界面: " + now);
        }
        catch { }
    }

    // 手动模式探针：打印手牌数值（伤害/格挡/费用/变量）+ 玩家能力，验证读得对不对
    static string _handProbeSig = "";
    static void TickHandProbe()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            RunOnMain(new Action(() =>
            {
                try
                {
                    object state = StateNow();
                    object player = Player0();
                    if (state == null || player == null) return;
                    object pcs = Prop(player, "PlayerCombatState");
                    if (pcs == null) return;

                    List<object> hand = L(Prop(Prop(pcs, "Hand"), "Cards"));
                    if (hand.Count == 0) return;
                    sb.Append("能量=").Append(I(pcs, "Energy")).Append(" 能力[");
                    List<object> ps = L(Prop(Prop(player, "Creature"), "Powers"));
                    bool firstP = true;
                    for (int i = 0; i < ps.Count; i++)
                    {
                        string nm = Loc(Prop(ps[i], "Title"));
                        int amt = I(ps[i], "Amount");
                        if (amt == 0) continue;
                        if (!firstP) sb.Append(',');
                        firstP = false;
                        sb.Append(nm).Append('=').Append(amt);
                    }
                    sb.Append("] ");
                    for (int i = 0; i < hand.Count; i++)
                    {
                        object c = hand[i];
                        if (i > 0) sb.Append(" | ");
                        int dm, hi, bl;
                        bool ex;
                        CardNumbers(c, player, state, out dm, out hi, out bl, out ex);
                        sb.Append(S(Prop(c, "Title"))).Append(" 费").Append(CardCostReal(c))
                          .Append(" 伤").Append(dm).Append('x').Append(hi).Append(" 挡").Append(bl);
                        string vars = VarsJson(c);
                        if (!string.IsNullOrEmpty(vars)) sb.Append(vars.Replace(",\"vars\":", " vars"));
                        if (!ex) sb.Append(" (估算)");
                    }
                }
                catch (Exception e) { sb.Append("探针失败:").Append(e.Message); }
            }));
            string now = sb.ToString();
            if (now.Length == 0 || now == _handProbeSig) return;
            _handProbeSig = now;
            W("手牌探针: " + now);
        }
        catch { }
    }

    // 手动模式探针：每 2 秒打印一次敌人的意图与能力（验证削弱前后数值变化）
    static string _intentSig = "";
    static void TickIntentProbe()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            RunOnMain(new Action(() =>
            {
                try
                {
                    object state = StateNow();
                    if (state == null) return;
                    List<object> enemies = L(Prop(state, "Enemies"));
                    for (int i = 0; i < enemies.Count; i++)
                    {
                        object e = enemies[i];
                        if (i > 0) sb.Append(" | ");
                        sb.Append(S(Prop(e, "Name"))).Append(" hp=").Append(I(e, "CurrentHp"));
                        sb.Append(" intent=").Append(IntentJson(e, Player0()));
                        StringBuilder pw = new StringBuilder();
                        try
                        {
                            IEnumerable ps = Prop(e, "Powers") as IEnumerable;
                            if (ps != null)
                                foreach (object p2 in ps)
                                {
                                    string nn = Loc(Prop(p2, "Title"));
                                    if (string.IsNullOrEmpty(nn)) nn = S(Prop(p2, "Id"));
                                    int amt = I(p2, "Amount");
                                    if (pw.Length > 0) pw.Append(',');
                                    pw.Append(nn).Append('=').Append(amt);
                                }
                        }
                        catch { }
                        if (pw.Length > 0) sb.Append(" powers[").Append(pw).Append(']');
                        // 毒伤预览（和正式 payload 同一套算法）
                        try
                        {
                            int po = PowerAmount(e, new string[] { "中毒", "Poison" });
                            if (po > 0)
                            {
                                int cat = HasRelicOrPower(Player0(), "触媒", "ACCELERANT");
                                sb.Append(" 毒{stacks:").Append(po).Append(",catalyst:").Append(cat)
                                  .Append(",endTurnDamage:").Append(po * (1 + cat)).Append('}');
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }));
            string now = sb.ToString();
            if (now.Length == 0 || now == _intentSig) return;
            _intentSig = now;
            W("意图探针: " + now);
        }
        catch { }
    }

    // 手动模式也能跑的可打出自检
    static void TickPlayProbe()
    {
        try
        {
            object player = null;
            object pcs = null;
            RunOnMain(new Action(() =>
            {
                try
                {
                    player = Player0();
                    pcs = player == null ? null : Prop(player, "PlayerCombatState");
                }
                catch { }
            }));
            if (pcs == null) return;
            ProbePlayability(pcs);
        }
        catch { }
    }

    // 每回合统计可打出的牌（便于排查）
    static string _probeSig = "";
    static void ProbePlayability(object pcs)
    {
        try
        {
            List<object> hand = L(Prop(Prop(pcs, "Hand"), "Cards"));
            StringBuilder sig = new StringBuilder();
            for (int i = 0; i < hand.Count; i++) sig.Append(S(Prop(hand[i], "Title"))).Append('|');
            if (sig.ToString() == _probeSig) return;
            _probeSig = sig.ToString();

            int ok = 0;
            StringBuilder bad = new StringBuilder();
            for (int i = 0; i < hand.Count; i++)
            {
                string why;
                if (CardCanPlay(hand[i], out why)) ok++;
                else bad.Append(S(Prop(hand[i], "Title"))).Append('(').Append(why).Append(") ");
            }
            W("可打出统计: " + ok + "/" + hand.Count + (bad.Length > 0 ? " 不可打: " + bad : ""));
        }
        catch (Exception e) { W("可打出统计失败: " + e.Message); }
    }

    // ---------- 沙坑（Boss 身上的倒计时 debuff） ----------
    static readonly Dictionary<int, double> _sandpitScore = new Dictionary<int, double>();
    static bool _sandpitLoaded;

    static void LoadSandpitRules()
    {
        if (_sandpitLoaded) return;
        _sandpitLoaded = true;
        try
        {
            string path = Paths.Kb + @"\sandpit.tsv";
            if (!File.Exists(path)) return;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = 1; i < lines.Length; i++)
            {
                string[] f = lines[i].Split('\t');
                if (f.Length < 2) continue;
                int lay; double sc;
                if (int.TryParse(f[0].Trim(), out lay) && double.TryParse(f[1].Trim(), out sc))
                    _sandpitScore[lay] = sc;
            }
            W("沙坑规则已载入: " + _sandpitScore.Count + " 档");
        }
        catch (Exception e) { W("沙坑规则载入失败: " + e.Message); }
    }

    static int SandpitAmount(object state)
    {
        try
        {
            List<object> enemies = L(Prop(state, "Enemies"));
            int best = -1;
            for (int i = 0; i < enemies.Count; i++)
            {
                object cre = enemies[i];
                object powers = Prop(cre, "Powers") ?? Field(cre, "_powers");
                IEnumerable seq = powers as IEnumerable;
                if (seq == null) continue;
                foreach (object pw in seq)
                {
                    if (pw == null) continue;
                    string t = Loc2(Prop(pw, "Title"));
                    if (t == null || t.IndexOf("沙坑", StringComparison.Ordinal) < 0) continue;
                    int a = I(pw, "Amount");
                    if (a <= 0) continue;
                    if (best < 0 || a < best) best = a;
                }
            }
            return best;
        }
        catch { return -1; }
    }

    static double SandpitCardScore(int layers)
    {
        LoadSandpitRules();
        if (layers <= 0) return -1;
        if (_sandpitScore.ContainsKey(layers)) return _sandpitScore[layers];
        if (layers > 6) return 0;
        return 0;
    }

    static string SandpitWhy(int layers)
    {
        if (layers <= 0) return "";
        if (layers == 1) return "沙坑 1 层，这回合结束就会死";
        if (layers >= 6) return "沙坑 " + layers + " 层，还早";
        return "沙坑 " + layers + " 层，" + layers + "回合后就死";
    }

    static int _lastSandpit = -99;
    static void TickSandpit()
    {
        try
        {
            object state = StateNow();
            if (state == null) return;
            int v = SandpitAmount(state);
            if (v == _lastSandpit) return;
            _lastSandpit = v;
            if (v > 0) W("沙坑检测: " + v + " 层（" + SandpitWhy(v) + "）");
            else W("沙坑检测: 未发现");
        }
        catch { }
    }

    // ---------- 结算界面（失败/通关）----------
    static bool _gameOverLogged;
    public static volatile bool _runEnded;   // 结算界面出现后置位：别再点、别再决策
    // 给 ClickSeat 用：结算界面出现后不许再点
    public static bool RunEndedNow()
    {
        return _runEnded;
    }

    static void TickGameOver()
    {
        if (_gameOverLogged) return;
        try
        {
            string found = "";
            RunOnMain(new Action(() =>
            {
                try
                {
                    SceneTree tree = Engine.GetMainLoop() as SceneTree;
                    if (tree == null) return;
                    List<Node> hits = new List<Node>();
                    CollectByTypeName(tree.Root, "NGameOverScreen", hits, 0);
                    CanvasItem ci = hits.Count > 0 ? hits[0] as CanvasItem : null;
                    if (hits.Count > 0 && (ci == null || ci.IsVisibleInTree())) found = "NGameOverScreen";
                }
                catch { }
            }));
            if (found.Length == 0) return;

            _gameOverLogged = true;
            _runEnded = true;              // 立刻停止一切点击与决策
            W("结算界面出现 → 停止 AutoSlay 的一切点击");
            string result = "unknown";
            int floor = 0;
            string charId = "";
            RunOnMain(new Action(() =>
            {
                try
                {
                    object rs = RunStateNow();
                    if (rs != null)
                    {
                        floor = FloorOf(rs);
                        charId = CharId();
                        object gt = Prop(rs, "GameOverType") ?? Prop(rs, "Result");
                        string g = gt == null ? "" : Convert.ToString(gt);
                        if (g.IndexOf("Victory", StringComparison.OrdinalIgnoreCase) >= 0 || g.IndexOf("Win", StringComparison.OrdinalIgnoreCase) >= 0) result = "win";
                        else if (g.Length > 0) result = "death";
                    }
                }
                catch (Exception ex) { W("结算读取失败: " + ex.Message); }
            }));

            // 结算界面出现时，未自动判定就按"有结果=死/通关"兜底
            if (result == "unknown") result = "death";
            W("结算界面: " + result + " 角色=" + charId + " 楼层=" + floor);
            PostRunEnd(floor, result, charId);
        }
        catch { }
    }

    // 判断这次选牌的"方向"：优先用游戏自带的 Prompt 语义，其次退回屏幕类型
    static string PromptTextOf(object prefs)
    {
        try
        {
            object p = Prop(prefs, "Prompt");
            if (p == null) return "";
            return Loc2(p);
        }
        catch { return ""; }
    }

    static string CanonPrompt(Assembly a, string prop)
    {
        try
        {
            Type t = a.GetType("MegaCrit.Sts2.Core.CardSelection.CardSelectorPrefs", false);
            if (t == null) return "";
            PropertyInfo pi = t.GetProperty(prop, BindingFlags.Public | BindingFlags.Static);
            if (pi == null) return "";
            object v = pi.GetValue(null, null);
            return v == null ? "" : Loc2(v);
        }
        catch { return ""; }
    }

    // 在场景树里找"当前可见的选牌屏"（覆盖栈里没有的那些也找得到）
    static object FindSelectionScreen(out string typeName)
    {
        object found = null;
        string name = "";
        RunOnMain(new Action(() =>
        {
            try
            {
                SceneTree tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null) return;
                string[] kinds = new string[] {
                    "NDeckUpgradeSelectScreen", "NDeckEnchantSelectScreen",
                    "NDeckTransformSelectScreen", "NDeckCardSelectScreen",
                    "NChooseACardSelectionScreen", "NSimpleCardSelectScreen",
                    "NCardGridSelectionScreen", "NCombatPileCardSelectScreen"
                };
                for (int k = 0; k < kinds.Length && found == null; k++)
                {
                    List<Node> hits = new List<Node>();
                    CollectByTypeName(tree.Root, kinds[k], hits, 0);
                    for (int i = 0; i < hits.Count; i++)
                    {
                        CanvasItem ci = hits[i] as CanvasItem;
                        if (ci != null && ci.IsVisibleInTree())
                        {
                            found = hits[i];
                            name = kinds[k];
                            break;
                        }
                    }
                }
            }
            catch { }
        }));
        typeName = name;
        return found;
    }

    // 用 AutoSlay 自己的方式取"当前屏幕"（权威、不需要扫场景树）
    static object CurrentScreen(string screenTypeName)
    {
        object scr = null;
        RunOnMain(new Action(() =>
        {
            try
            {
                Type as2 = _sts2.GetType("MegaCrit.Sts2.Core.AutoSlay.AutoSlayer", false);
                Type st = _sts2.GetType("MegaCrit.Sts2.Core.Nodes.Screens." + screenTypeName, false);
                if (st == null)
                {
                    // 选牌屏在 CardSelection 子命名空间
                    st = _sts2.GetType("MegaCrit.Sts2.Core.Nodes.Screens.CardSelection." + screenTypeName, false);
                }
                if (as2 == null || st == null) return;
                MethodInfo gcs = null;
                foreach (MethodInfo mi in as2.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (mi.Name == "GetCurrentScreen" && mi.IsGenericMethodDefinition) { gcs = mi; break; }
                }
                if (gcs == null) return;
                scr = gcs.MakeGenericMethod(st).Invoke(null, null);
            }
            catch { }
        }));
        return scr;
    }

    static readonly string[] SelectionScreenNames = new string[] {
        "NDeckUpgradeSelectScreen", "NDeckEnchantSelectScreen", "NDeckTransformSelectScreen",
        "NDeckCardSelectScreen", "NChooseACardSelectionScreen", "NChooseABundleSelectionScreen",
        "NSimpleCardSelectScreen", "NCardRewardSelectionScreen", "NCombatPileCardSelectScreen"
    };

    // 按 AutoSlay 注册表找"当前是哪个选牌屏"
    static string CurrentSelectionScreen(out object screen)
    {
        screen = null;
        for (int i = 0; i < SelectionScreenNames.Length; i++)
        {
            object sc = CurrentScreen(SelectionScreenNames[i]);
            if (sc != null) { screen = sc; return SelectionScreenNames[i]; }
        }
        return "";
    }

    // 探针：打印选牌时的调用栈 + 场景里可能的选牌屏节点
    static int _selProbeCount;
    static void ProbeSelectionContext(string top, string prompt)
    {
        if (_selProbeCount >= 12) return;
        _selProbeCount++;
        try
        {
            W("选牌探针 ===== 第 " + _selProbeCount + " 次 =====");
            W("  已判定: top='" + top + "' prompt='" + (string.IsNullOrEmpty(prompt) ? "(空)" : prompt) + "'");
            // 调用栈（前 12 帧）
            try
            {
                System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(1, false);
                for (int i = 0; i < st.FrameCount && i < 12; i++)
                {
                    System.Diagnostics.StackFrame fr = st.GetFrame(i);
                    MethodBase mb = fr == null ? null : fr.GetMethod();
                    if (mb == null) continue;
                    W("  栈[" + i + "] " + mb.DeclaringType.FullName + "::" + mb.Name);
                }
            }
            catch (Exception e) { W("  栈失败: " + e.Message); }

            // 场景里含 Select/Grid/Upgrade/Enchant/Removal 的可见节点
            StringBuilder sb2 = new StringBuilder();
            RunOnMain(new Action(() =>
            {
                try
                {
                    SceneTree tree = Engine.GetMainLoop() as SceneTree;
                    if (tree == null) return;
                    CollectScreenNames(tree.Root, sb2, 0, 0);
                }
                catch { }
            }));
            W("  可见屏: " + (sb2.Length == 0 ? "(无)" : sb2.ToString()));
        }
        catch { }
    }

    static void CollectScreenNames(Node n, StringBuilder acc, int depth, int count)
    {
        if (n == null || depth > 12 || acc.Length > 700 || count > 40) return;
        try
        {
            string tn = n.GetType().Name;
            CanvasItem ci = n as CanvasItem;
            bool vis = ci == null || ci.IsVisibleInTree();
            if (vis && (tn.IndexOf("Select", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        tn.IndexOf("Grid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        tn.IndexOf("Upgrade", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        tn.IndexOf("Enchant", StringComparison.OrdinalIgnoreCase) >= 0))
                acc.Append(tn).Append(' ');
            int c = 0;
            try { c = n.GetChildCount(); } catch { return; }
            for (int i = 0; i < c; i++)
            {
                Node ch = null;
                try { ch = n.GetChild(i); } catch { }
                CollectScreenNames(ch, acc, depth + 1, count + 1);
            }
        }
        catch { }
    }

    static string SelectionPurpose()
    {
        string top = "";
        string prompt = "";
        RunOnMain(new Action(() =>
        {
            try
            {
                // 先用 AutoSlay 的"当前屏幕"注册表（权威）
                object selScreen;
                string selName = CurrentSelectionScreen(out selScreen);
                if (selScreen != null)
                {
                    top = selName;
                    object prefsA = Prop(selScreen, "Prefs") ?? Field(selScreen, "_prefs");
                    if (prefsA != null) prompt = PromptTextOf(prefsA);
                    return;
                }

                object stack = OverlayStack();
                object ov = null;
                if (stack != null && I(stack, "ScreenCount") > 0)
                {
                    try { ov = stack.GetType().GetMethod("Peek").Invoke(stack, null); } catch { }
                }
                if (ov != null)
                {
                    top = ov.GetType().Name;
                    object prefs = Prop(ov, "Prefs") ?? Field(ov, "_prefs");
                    if (prefs != null) prompt = PromptTextOf(prefs);
                }
                // 覆盖栈拿不到 → 在场景树里找可见的选牌屏
                if (ov == null || string.IsNullOrEmpty(top))
                {
                    string tn;
                    object sc = FindSelectionScreen(out tn);
                    if (sc != null)
                    {
                        top = tn;
                        object prefs2 = Prop(sc, "Prefs") ?? Field(sc, "_prefs");
                        if (prefs2 != null) prompt = PromptTextOf(prefs2);
                    }
                }
            }
            catch { }
        }));

        // 1) 用游戏自带的提示语判定（最可靠）
        if (!string.IsNullOrEmpty(prompt))
        {
            string[,] map = new string[,] {
                {"UpgradeSelectionPrompt", "upgrade"},
                {"RemoveSelectionPrompt", "remove"},
                {"TransformSelectionPrompt", "transform"},
                {"DiscardSelectionPrompt", "discard"},
                {"ExhaustSelectionPrompt", "exhaust"},
                {"EnchantSelectionPrompt", "enchant"}
            };
            for (int i = 0; i < map.GetLength(0); i++)
            {
                string canon = CanonPrompt(_sts2, map[i, 0]);
                if (!string.IsNullOrEmpty(canon) && prompt.IndexOf(canon.Trim(), StringComparison.Ordinal) >= 0)
                {
                    W("选牌屏: " + top + "  提示: " + prompt + "  → " + map[i, 1]);
                    return map[i, 1];
                }
            }
        }

        // 2) 退回屏幕类型
        string byType = "unknown";
        if (top == "NCardRewardSelectionScreen") byType = "reward";
        else if (top == "NDeckUpgradeSelectScreen") byType = "upgrade";
        else if (top == "NDeckEnchantSelectScreen") byType = "enchant";
        else if (top == "NDeckTransformSelectScreen") byType = "transform";
        else if (top == "NChooseABundleSelectionScreen" || top == "NChooseACardSelectionScreen" || top == "NSimpleCardSelectScreen")
        {
            // 这几类是"从若干张里拿牌"，按拿牌处理
            byType = "reward";
        }
        else if (top != "" && top.IndexOf("Remove", StringComparison.OrdinalIgnoreCase) >= 0) byType = "remove";
        else
        {
            bool inCombat = false;
            RunOnMain(new Action(() =>
            {
                try { inCombat = (RoomTypeText(RunStateNow()) ?? "").IndexOf("Monster", StringComparison.OrdinalIgnoreCase) >= 0; }
                catch { }
            }));
            byType = inCombat ? "discard" : "remove";
        }
        W("选牌屏: " + top + "  提示: " + (string.IsNullOrEmpty(prompt) ? "(空)" : prompt) + "  → " + byType + "（按类型兜底）");
        ProbeSelectionContext(top, prompt);
        return byType;
    }

    static bool _driverPassLogged;
    static readonly string[] DriverScreenNames = new string[] {
        "NDeckUpgradeSelectScreen", "NDeckEnchantSelectScreen", "NDeckTransformSelectScreen",
        "NDeckCardSelectScreen", "NSimpleCardSelectScreen", "NChooseACardSelectionScreen"
    };
    static bool IsDriverScreen(string name)
    {
        for (int i = 0; i < DriverScreenNames.Length; i++)
            if (DriverScreenNames[i] == name) return true;
        return false;
    }



    // 后台预取地图决策：地图一打开就先问，等真正选点时答案已就绪
    static void PrefetchMap()
    {
        if (_mapAsking) return;
        NMapScreen screen = null;
        try
        {
            Type mt = _sts2.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen", false);
            PropertyInfo inst = mt.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            screen = inst == null ? null : inst.GetValue(null, null) as NMapScreen;
            if (screen == null) return;
            object open = Prop(screen, "IsOpen");
            if (open is bool && !(bool)open) return;
        }
        catch { return; }

        _mapAsking = true;
        NMapScreen s = screen;
        Task.Run(new Action(() =>
        {
            try
            {
                DictionaryEntry[] entries = null;
                List<string> coords = null;
                List<object> models = null;
                RunOnMain(new Action(() =>
                {
                    object dictObj = Field(s, "_mapPointDictionary");
                    IDictionary dict = dictObj as IDictionary;
                    if (dict == null) return;
                    coords = new List<string>();
                    models = new List<object>();
                    List<DictionaryEntry> es = new List<DictionaryEntry>();
                    foreach (DictionaryEntry de in dict)
                    {
                        NMapPoint p = de.Value as NMapPoint;
                        if (p == null) continue;
                        object trav = Prop(p, "IsTravelable");
                        if (!(trav is bool) || !(bool)trav) continue;
                        es.Add(de); coords.Add(S(de.Key)); models.Add(Prop(p, "Point"));
                    }
                    entries = es.ToArray();
                }));
                if (entries == null || entries.Length < 2) return;

                string key = string.Join("|", coords.ToArray());
                if (_mapKey == key && _mapPick != null) return;

                int id = Interlocked.Increment(ref _seq);
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"id\":").Append(id).Append(",\"kind\":\"pick_map\",\"nodes\":[");
                for (int i = 0; i < entries.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    object m = models[i];
                    sb.Append("{\"i\":").Append(i).Append(",\"coord\":\"").Append(J(coords[i])).Append('"')
                      .Append(",\"type\":\"").Append(J(S(Prop(m, "PointType") ?? Prop(m, "Type") ?? Prop(m, "RoomType")))).Append("\"}");
                }
                sb.Append("]}");

                string resp = Ask(sb.ToString(), "pick_map(prefetch)");
                Match mm = Regex.Match(resp ?? "", "\"index\"\\s*:\\s*(-?\\d+)");
                int pick = mm.Success ? int.Parse(mm.Groups[1].Value) : -1;
                if (pick < 0 || pick >= entries.Length) return;

                _mapPick = entries[pick].Value as NMapPoint;
                _mapKey = key;
                W("地图预取就绪: " + coords[pick]);
            }
            catch (Exception e) { W("预取失败: " + e.Message); }
            finally { _mapAsking = false; }
        }));
    }

    static object MakeChoiceContext(object sync)
    {
        Type t = _sts2.GetType("MegaCrit.Sts2.Core.GameActions.Multiplayer.GameActionPlayerChoiceContext", false);
        if (t == null) return null;
        foreach (ConstructorInfo c in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            ParameterInfo[] ps = c.GetParameters();
            object[] args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                Type pt = ps[i].ParameterType;
                if (pt.Name == "ActionQueueSynchronizer") args[i] = sync;
                else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                else args[i] = null;
            }
            try { return c.Invoke(args); } catch { }
        }
        return null;
    }

    static bool EnqueuePlay(string cardTitle, int targetIdx)
    {
        object state = StateNow();
        object player = Player0();
        if (state == null || player == null) return false;
        object pcs = Prop(player, "PlayerCombatState");
        List<object> hand = L(Prop(Prop(pcs, "Hand"), "Cards"));
        object card = null;
        for (int i = 0; i < hand.Count; i++) { if (S(Prop(hand[i], "Title")) == cardTitle) { card = hand[i]; break; } }
        if (card == null) { W("出牌: 找不到「" + cardTitle + "」"); return false; }
        // 先问游戏这张牌现在能不能打（参考 dsh-sts2-main 的 Playability）
        string whyNot;
        if (!CardCanPlay(card, out whyNot))
        {
            W("出牌被拒: 「" + cardTitle + "」现在打不出去（" + (string.IsNullOrEmpty(whyNot) ? "条件不满足" : whyNot) + "）");
            return false;
        }
        object target = null;
        if (S(Prop(card, "TargetType")).IndexOf("Enemy", StringComparison.Ordinal) >= 0)
        {
            List<object> enemies = L(Prop(state, "Enemies"));
            if (enemies.Count == 0) return false;
            target = (targetIdx >= 0 && targetIdx < enemies.Count) ? enemies[targetIdx] : enemies[0];
        }
        Type pca = _sts2.GetType("MegaCrit.Sts2.Core.GameActions.PlayCardAction", false);
        object action = Activator.CreateInstance(pca, new object[] { card, target });
        object sync = Sync();
        if (sync == null) return false;
        object ctx = MakeChoiceContext(sync);
        if (ctx != null)
        {
            PropertyInfo sp = pca.GetProperty("PlayerChoiceContext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (sp != null && sp.CanWrite) sp.SetValue(action, ctx, null);
        }
        MethodInfo req = null;
        foreach (MethodInfo m in sync.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            if (m.Name == "RequestEnqueue" && m.GetParameters().Length == 1) { req = m; break; }
        if (req == null) return false;
        req.Invoke(sync, new object[] { action });
        W("出牌 → 入队「" + cardTitle + "」");
        return true;
    }

    static bool EndTurn()
    {
        try
        {
            object player = Player0();
            if (player == null) return false;
            Type pc = _sts2.GetType("MegaCrit.Sts2.Core.Commands.PlayerCmd", false);
            foreach (MethodInfo m in pc.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (m.Name != "EndTurn") continue;
                if (m.GetParameters().Length != 3) continue;
                // 参考 dsh-sts2-main：回调要传一个已完成的 Task，传 null 会让内部 await 空引用
                Func<Task> done = delegate { return Task.CompletedTask; };
                m.Invoke(null, new object[] { player, true, done });
                return true;
            }
        }
        catch (Exception e) { W("endturn fail: " + e.Message); }
        return false;
    }

    static string BuildCombatJson(object state, object player, object pcs)
    {
        int id = Interlocked.Increment(ref _seq);
        List<object> hand = L(Prop(Prop(pcs, "Hand"), "Cards"));
        List<object> enemies = L(Prop(state, "Enemies"));
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"id\":").Append(id).Append(",\"kind\":\"combat_turn\",\"char\":\"").Append(CharId()).Append("\",\"energy\":").Append(I(pcs, "Energy"))
          .Append(CharId() == "regent" ? (",\"stars\":" + I(pcs, "Stars")) : "")
          .Append(",\"turn\":").Append(I(pcs, "TurnNumber"));
          if (!HandComplete()) sb.Append(",\"handComplete\":false");
        object me = Prop(player, "Creature");
        sb.Append(",\"hp\":").Append(I(me, "CurrentHp")).Append(",\"maxHp\":").Append(I(me, "MaxHp")).Append(",\"block\":").Append(I(me, "Block"));
        AppendPlayerPowersJson(sb, player);
        try { if (HasRelic("ICE_CREAM")) sb.Append(",\"energyKeeps\":true"); } catch { }
        if (!_intentProbed) { _intentProbed = true; ProbeIntent(enemies); }
        ProbePlayability(pcs);
        StripCheats(player);
        int sandpit = SandpitAmount(StateNow());
        if (sandpit > 0) sb.Append(",\"sandpit\":").Append(sandpit);
        sb.Append(",\"threat\":\"").Append(J(ThreatText(enemies, player))).Append('"');

        sb.Append(",\"hand\":[");
        for (int i = 0; i < hand.Count; i++)
        {
            if (i > 0) sb.Append(',');
            object c = hand[i];
            object cost = Prop(c, "EnergyCost");
            sb.Append("{\"i\":").Append(i)
              .Append(",\"title\":\"").Append(J(S(Prop(c, "Title")))).Append('"')
              .Append(",\"cost\":\"").Append(J(cost == null ? "" : S(Prop(cost, "Canonical") ?? Prop(cost, "Current") ?? cost))).Append('"')
              .Append(",\"target\":\"").Append(J(S(Prop(c, "TargetType")))).Append('"');
            if (CharId() == "regent")
            {
                int starCost = CardStarCost(c);
                if (starCost > 0) sb.Append(",\"star\":").Append(starCost);
            if (S(Prop(c, "Title")) == "狂乱逃离")
            {
                int sp = SandpitAmount(StateNow());
                double ssc = SandpitCardScore(sp);
                if (ssc >= 0)
                {
                    sb.Append(",\"score\":").Append(ssc.ToString("0.##"))
                      .Append(",\"why\":\"").Append(J(SandpitWhy(sp))).Append('"');
                }
            }
            }

            // 奇巧（Sly）：本回合是否有该词条
            try
            {
                object slyNow = Prop(c, "IsSlyThisTurn");
                if (slyNow is bool && (bool)slyNow) sb.Append(",\"sly\":true");
            }
            catch { }

            string noReason;
            bool canPlayNow = CardCanPlay(c, out noReason);
            if (!canPlayNow)
            {
                sb.Append(",\"playable\":false");
                if (!string.IsNullOrEmpty(noReason)) sb.Append(",\"noPlay\":\"").Append(J(noReason)).Append('"');
            }

            int dm, hi, bl; bool ex;
            CardNumbers(c, player, state, out dm, out hi, out bl, out ex);

            // 短描述：知识库那份带数字；占位符 {Shivs:diff()} 用 vars 的真实数值填回去，
            // 再把没有出现在描述里的 vars 追加到末尾（这样就不必单独发 vars 了）
            string descText = "";
            CardInfo cinfoD = FindCardInfo(S(Prop(c, "Title")), CharId());
            if (cinfoD != null) descText = cinfoD.Desc;
            if (string.IsNullOrEmpty(descText))
            {
                try
                {
                    object dsc = Prop(c, "Description");
                    if (dsc != null) descText = Loc(dsc);
                }
                catch { }
            }
            if (!string.IsNullOrEmpty(descText))
            {
                Dictionary<string, int> vs = VarsDict(c);
                StringBuilder used = new StringBuilder();
                // ① 占位符 → 真实数值
                descText = Regex.Replace(descText, "\\{(\\w+):[^{}]*\\}", delegate(Match mm)
                {
                    string nm = mm.Groups[1].Value;
                    int val;
                    if (vs != null && vs.TryGetValue(nm, out val)) { used.Append(nm).Append(','); return val.ToString(); }
                    return "";
                });
                // ② 描述里没出现过的 vars，追加到末尾
                if (vs != null)
                {
                    StringBuilder extra = new StringBuilder();
                    foreach (KeyValuePair<string, int> kv in vs)
                    {
                        if (used.ToString().IndexOf(kv.Key + ",", StringComparison.Ordinal) >= 0) continue;
                        if (kv.Key == "Damage" || kv.Key == "Block") continue;
                        if (descText.IndexOf(kv.Value.ToString(), StringComparison.Ordinal) >= 0) continue;
                        if (extra.Length > 0) extra.Append(',');
                        extra.Append(kv.Key).Append('=').Append(kv.Value);
                    }
                    if (extra.Length > 0) descText = descText + "（另有 " + extra + "）";
                }
                descText = Regex.Replace(descText, "\\{[^{}]*\\}", "");
                descText = Regex.Replace(descText, "\\s+", " ").Trim();
                if (descText.Length > 90) descText = descText.Substring(0, 90);
                sb.Append(",\"desc\":\"").Append(J(descText)).Append('"');
            }
            if (dm > 0) sb.Append(",\"dmg\":").Append(dm).Append(",\"hits\":").Append(hi);
            try
            {
                object up = Prop(c, "IsUpgraded");
                if (up is bool && (bool)up) sb.Append(",\"up\":true");
                string whyNot;
                if (!CardCanPlay(c, out whyNot))
                {
                    sb.Append(",\"playable\":false");
                    if (!string.IsNullOrEmpty(whyNot)) sb.Append(",\"noPlay\":\"").Append(J(whyNot)).Append('"');
                }
            }
            catch { }
            if (bl > 0) sb.Append(",\"block\":").Append(bl);
            if (!ex) sb.Append(",\"exact\":false");

            // 实时费效：把当前敌人数量、X 费的真实花费都算进去
            int price = CardCost(c);
            if (price <= 0) price = 1;
            int enemyCount = L(Prop(state, "Enemies")).Count;
            bool aoe = CardIsAoe(c);
            int total = dm * Math.Max(1, hi) * (aoe ? Math.Max(1, enemyCount) : 1);
            double effNow = (total + bl * 1.2) / price;

            if (aoe) sb.Append(",\"aoe\":true");
            sb.Append('}');
        }
        sb.Append("]");
        AppendPotionsJson(sb, player);
        sb.Append(",\"enemies\":[");
        for (int i = 0; i < enemies.Count; i++)
        {
            if (i > 0) sb.Append(',');
            object e = enemies[i];
            sb.Append("{\"i\":").Append(i).Append(",\"name\":\"").Append(J(S(Prop(e, "Name")))).Append('"')
              .Append(",\"hp\":").Append(I(e, "CurrentHp")).Append(",\"block\":").Append(I(e, "Block"))
              .Append(IntentJson(e, player));
            try
            {
                // 毒：回合结束时会掉多少（毒层数 × (1 + 触媒层数)）
                try
                {
                    int poison = PowerAmount(e, new string[] { "中毒", "Poison" });
                    if (poison > 0)
                    {
                        int cat = HasRelicOrPower(player, "触媒", "ACCELERANT");
                        int perTurn = PoisonEndTurnDamage(poison, cat);
                        sb.Append(",\"poison\":{\"stacks\":").Append(poison)
                          .Append(",\"catalyst\":").Append(cat)
                          .Append(",\"endTurnDamage\":").Append(perTurn).Append('}');
                    }
                }
                catch { }
                int itsDmg = EnemyIntentTotal(e, player);
                int itsHp = I(e, "CurrentHp");
                int itsBlock = I(e, "Block");
                if (itsDmg > 0 && itsHp > 0)
                {
                    int need = itsHp + itsBlock;
                    sb.Append(",\"killGain\":\"打掉它需 ").Append(need)
                      .Append(" 点伤害，之后少挨 ").Append(itsDmg).Append(" 点");
                      int pd = PowerAmount(e, new string[] { "中毒", "Poison" });
                      if (pd > 0) sb.Append("（回合结束还会掉 ").Append(PoisonEndTurnDamage(pd, HasRelicOrPower(player, "触媒", "ACCELERANT"))).Append(" 点毒伤）");
                      sb.Append('"');   // 统一收尾：毒伤提示也在引号内
                }
            }
            catch { }
            StringBuilder pw = new StringBuilder("[");
            int pwn = 0;
            try
            {
                object creep = e;
                IEnumerable pseq = (Prop(creep, "Powers") ?? Field(creep, "_powers")) as IEnumerable;
                if (pseq != null)
                    foreach (object p in pseq)
                    {
                        if (p == null) continue;
                        string pn = Loc2(Prop(p, "Title"));
                        int pa = I(p, "Amount");
                        if (string.IsNullOrEmpty(pn) || pa == 0) continue;
                        if (pwn > 0) pw.Append(',');
                        pw.Append("{\"n\":\"").Append(J(pn)).Append("\",\"a\":").Append(pa).Append('}');
                        pwn++;
                        if (pwn >= 8) break;
                    }
            }
            catch { }
            pw.Append(']');
            sb.Append(",\"powers\":").Append(pw).Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    // 一个回合：反复问"下一个动作"，直到她说 end
    static void RunTurn()
    {
        object p0 = Player0();
        object c0 = p0 == null ? null : Prop(p0, "PlayerCombatState");
        int turn = c0 == null ? -1 : TurnNo(c0);

        int guard = 0;
        while (guard++ < 12)
        {
            string ask = null;
            bool playable = false;
            RunOnMain(new Action(() =>
            {
                object player = Player0();
                object pcs = player == null ? null : Prop(player, "PlayerCombatState");
                if (pcs == null || PhaseText(pcs) != "Play") return;
                playable = true;
                ask = BuildCombatJson(StateNow(), player, pcs);
            }));
            if (!playable || ask == null) return;
            if (guard == 1)
            {
                int ei = ask.IndexOf("\"enemies\"");
                if (ei > 0) W("敌人: " + ask.Substring(ei, Math.Min(400, ask.Length - ei)));
            }
            if (guard == 1) W("看局面: " + ask.Substring(0, Math.Min(2000, ask.Length)));
            _lastAskSig = CombatSig();

            // 防抖：先看"有没有牌能打"，没有就等 400ms 再确认一次，仍没有就直接结束回合
            bool noPlay = false;
            RunOnMain(new Action(() =>
            {
                object player = Player0();
                object pcs = player == null ? null : Prop(player, "PlayerCombatState");
                if (pcs == null) return;
                noPlay = !HasPlayable(pcs) && I(pcs, "Energy") <= 0 && I(pcs, "Stars") <= 0;
            }));
            if (noPlay)
            {
                Thread.Sleep(400);
                bool stillNoPlay = false;
                RunOnMain(new Action(() =>
                {
                    object player = Player0();
                    object pcs = player == null ? null : Prop(player, "PlayerCombatState");
                    if (pcs == null) return;
                    stillNoPlay = !HasPlayable(pcs) && I(pcs, "Energy") <= 0 && I(pcs, "Stars") <= 0;
                }));
                if (stillNoPlay)
                {
                    W("没费用且没有 0 费牌（防抖确认）→ 直接结束回合 " + turn);
                    return;
                }
            }

            // 发送前自检：JSON 坏了当场说清楚（否则插件那边只会报 Expected ','）
            try
            {
                using (System.IO.MemoryStream ms = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(ask)))
                {
                    System.Runtime.Serialization.Json.DataContractJsonSerializer ser =
                        new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(object));
                    ser.ReadObject(ms);
                }
            }
            catch (Exception je)
            {
                int pos = ask.IndexOf("\"killGain\"");
                W("[自检] combat payload JSON 非法: " + je.Message + "（killGain 在 " + pos + " 字节处）");
            }

            string resp;
            try { resp = Ask(ask, "combat"); }
            catch (Exception e) { W("动作决策失败: " + e.Message); return; }

            if (Regex.IsMatch(resp, "\"end\"\\s*:\\s*true"))
            {
                // 只拦一次做防抖：有能量 + 有能打的牌时提醒她再想一下（有冰淇淋就不拦，允许留费）
                if (!HasRelic("ICE_CREAM"))
                {
                    int energyNow = 0, playableNow = 0;
                    RunOnMain(new Action(() =>
                    {
                        object player = Player0();
                        object pcs = player == null ? null : Prop(player, "PlayerCombatState");
                        if (pcs == null) return;
                        energyNow = I(pcs, "Energy");
                        List<object> hand = L(Prop(Prop(pcs, "Hand"), "Cards"));
                        for (int q = 0; q < hand.Count; q++) { string why; if (CardCanPlay(hand[q], out why)) playableNow++; }
                    }));

                    if (energyNow > 0 && playableNow > 0 && _endGuardTurn != turn)
                    {
                        _endGuardTurn = turn;
                        W("拦一次：还有 " + energyNow + " 能量、" + playableNow + " 张能打的牌，再问一次");
                        string ask2 = ask.TrimEnd();
                        if (ask2.EndsWith("}")) ask2 = ask2.Substring(0, ask2.Length - 1) + ",\"reminder\":\"你还有 " + energyNow + " 点能量和 " + playableNow + " 张能打的牌，费用无法保留到下回合，能打就打；确定要结束就再回一次 end。\"}";
                        try { resp = Ask(ask2, "combat"); }
                        catch (Exception e) { W("再问失败: " + e.Message); }
                    }
                }

                if (Regex.IsMatch(resp ?? "", "\"end\"\\s*:\\s*true")) { W("她说够了 → 结束回合 " + turn); return; }
            }

            // 喝药水 / 丢药水
            Match pm = Regex.Match(resp ?? "", "\"potion\"\\s*:\\s*(\\d+)");
            if (pm.Success)
            {
                int pi = int.Parse(pm.Groups[1].Value);
                Match pt = Regex.Match(resp ?? "", "\"t\"\\s*:\\s*(-?\\d+)");
                int pti = pt.Success ? int.Parse(pt.Groups[1].Value) : -1;
                object pl = Player0();
                if (UsePotionAt(pl, pi, pti)) { Thread.Sleep(600); continue; }
                W("喝药水没成功，继续决策");
                continue;
            }
            Match dm = Regex.Match(resp ?? "", "\"dropPotion\"\\s*:\\s*(\\d+)");
            if (dm.Success)
            {
                int di = int.Parse(dm.Groups[1].Value);
                if (DiscardPotionAt(Player0(), di)) { Thread.Sleep(400); continue; }
                continue;
            }

            Match m = Regex.Match(resp, "\"i\"\\s*:\\s*(\\d+)\\s*,\\s*\"t\"\\s*:\\s*(-?\\d+)");
            if (!m.Success) { W("动作解析失败: " + resp); return; }
            int ci = int.Parse(m.Groups[1].Value);
            int ti = int.Parse(m.Groups[2].Value);

            bool queued = false;
            int handBefore = 0;
            RunOnMain(new Action(() =>
            {
                object player = Player0();
                object pcs = player == null ? null : Prop(player, "PlayerCombatState");
                if (pcs == null || PhaseText(pcs) != "Play") return;
                List<object> hand = L(Prop(Prop(pcs, "Hand"), "Cards"));
                if (ci < 0 || ci >= hand.Count) return;
                handBefore = hand.Count;
                queued = EnqueuePlay(S(Prop(hand[ci], "Title")), ti);
            }));
            if (!queued) return;

            for (int i = 0; i < 25; i++)
            {
                Thread.Sleep(120);
                bool changed = false;
                RunOnMain(new Action(() =>
                {
                    object p = Player0();
                    object c = p == null ? null : Prop(p, "PlayerCombatState");
                    if (c == null) { changed = true; return; }
                    if (L(Prop(Prop(c, "Hand"), "Cards")).Count != handBefore) changed = true;
                }));
                if (changed) break;
            }
        }
    }

    // 被"调用点改写"后进入的出牌钩子（签名必须与 CardCmd.AutoPlay 完全一致）
    public static Task HookAutoPlay(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext ctx,
                                    MegaCrit.Sts2.Core.Models.CardModel card,
                                    MegaCrit.Sts2.Core.Entities.Creatures.Creature target,
                                    MegaCrit.Sts2.Core.Entities.Cards.AutoPlayType type,
                                    bool a, bool b)
    {
        try
        {
            if (!AgentMode())
                return MegaCrit.Sts2.Core.Commands.CardCmd.AutoPlay(ctx, card, target, type, a, b);

            if (_runTurnActive) return Task.CompletedTask;   // 本回合已在决策，忽略重复调用
            _runTurnActive = true;
            if (_autoPlayDbg < 6) { _autoPlayDbg++; W("HookAutoPlay 进入（调用点改写生效）"); }
            Task work = Task.Run(new Action(() =>
            {
                try
                {
                    RunTurn();

                    // B：结束回合前复查——只有"局面变了"（抽牌晚到）才重问，最多 1 次
                    string sigBefore = _lastAskSig;
                    Thread.Sleep(400);                     // 等延迟抽牌结算完
                    string sigAfter = CombatSig();
                    if (!string.IsNullOrEmpty(sigBefore) && sigAfter != sigBefore)
                    {
                        bool playable = false;
                        RunOnMain(new Action(() =>
                        {
                            object p = Player0();
                            object pcs = p == null ? null : Prop(p, "PlayerCombatState");
                            if (pcs == null || PhaseText(pcs) != "Play") return;
                            playable = HasPlayable(pcs);
                        }));
                        if (playable)
                        {
                            W("抽牌晚到 → 局面变了，再决策一次");
                            RunTurn();
                        }
                    }

                    int turn = -1;
                    RunOnMain(new Action(() =>
                    {
                        object player = Player0();
                        object pcs = player == null ? null : Prop(player, "PlayerCombatState");
                        if (pcs == null || PhaseText(pcs) != "Play") return;
                        turn = TurnNo(pcs);
                        if (_endedTurn != turn && EndTurn()) { _endedTurn = turn; W("结束回合 " + turn); }
                    }));

                    Thread.Sleep(800);
                }
                catch (Exception e) { W("RunTurn 异常: " + e.Message); }
                finally { _runTurnActive = false; }
            }));
            return work;
        }
        catch (Exception e)
        {
            W("HookAutoPlay 异常: " + e.Message);
            return Task.CompletedTask;
        }
    }

    static bool PrefixAutoPlay(object[] __args, ref Task __result)
    {
        if (_autoPlayDbg < 6) { _autoPlayDbg++; W("AutoPlay 进入: agentMode=" + AgentMode() + " fallback=" + _inFallback); }
        if (!AgentMode()) return true;
        if (_inFallback) return true;
        __result = Task.Run(new Action(() =>
        {
            try
            {
                RunTurn();

                int turn = -1;
                RunOnMain(new Action(() =>
                {
                    object player = Player0();
                    object pcs = player == null ? null : Prop(player, "PlayerCombatState");
                    if (pcs == null || PhaseText(pcs) != "Play") return;
                    turn = TurnNo(pcs);
                    if (_endedTurn != turn && EndTurn()) { _endedTurn = turn; W("结束回合 " + turn); }
                }));

                Thread.Sleep(800);
            }
            catch (Exception e) { W("AutoPlay 异常: " + e.Message); }
        }));
        return false;
    }

    // CardSelectCmd 的各个入口是 async，状态机类型名里带用途（<FromDeckForUpgrade>d__21）
    // CardSelectCmd 的各个入口是 async，状态机类型名里带用途（<FromDeckForUpgrade>d__21）
    // 但 FromDeckGeneric 名字里不带方向 —— 去读状态机里的 <prefs> 字段（CardSelectorPrefs），
    // 它带 RemoveSelectionPrompt / UpgradeSelectionPrompt 这些专用提示，最准确。
    static void PrefixSelectPurpose(object __instance)
    {
        try
        {
            string tn = __instance == null ? "" : __instance.GetType().Name;

            // ① 先从入口方法名推（明确的那几种）
            string byName = null;
            if (tn.IndexOf("Upgrade", StringComparison.OrdinalIgnoreCase) >= 0) byName = "upgrade";
            else if (tn.IndexOf("Enchant", StringComparison.OrdinalIgnoreCase) >= 0) byName = "enchant";
            else if (tn.IndexOf("Transform", StringComparison.OrdinalIgnoreCase) >= 0) byName = "transform";
            else if (tn.IndexOf("Removal", StringComparison.OrdinalIgnoreCase) >= 0) byName = "remove";
            else if (tn.IndexOf("Discard", StringComparison.OrdinalIgnoreCase) >= 0) byName = "discard";
            else if (tn.IndexOf("SimpleGrid", StringComparison.OrdinalIgnoreCase) >= 0) byName = "reward";
            else if (tn.IndexOf("ChooseACard", StringComparison.OrdinalIgnoreCase) >= 0) byName = "reward";
            else if (tn.IndexOf("FromHand", StringComparison.OrdinalIgnoreCase) >= 0) byName = "discard";

            if (!string.IsNullOrEmpty(byName)) { _pendingPurpose = byName; return; }

            // ② 名字没方向（FromDeckGeneric 等）→ 读 prefs
            string byPrefs = PurposeFromPrefs(__instance);
            if (!string.IsNullOrEmpty(byPrefs)) { _pendingPurpose = byPrefs; return; }

            _pendingPurpose = "simple";
        }
        catch { }
    }

    // 从状态机的 <prefs> 字段读出 CardSelectorPrefs，按提示词判方向
    static string PurposeFromPrefs(object stateMachine)
    {
        try
        {
            if (stateMachine == null) return null;
            object prefs = null;
            foreach (FieldInfo fi in stateMachine.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (fi.Name.IndexOf("prefs", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try { prefs = fi.GetValue(stateMachine); } catch { }
                if (prefs != null) break;
            }
            if (prefs == null) return null;

            // 专用字段优先，再退通用 Prompt
            string[] fields = new string[] { "RemoveSelectionPrompt", "UpgradeSelectionPrompt", "TransformSelectionPrompt",
                                             "EnchantSelectionPrompt", "DiscardSelectionPrompt", "ExhaustSelectionPrompt", "Prompt" };
            for (int i = 0; i < fields.Length; i++)
            {
                object v = Prop(prefs, fields[i]);
                string txt = v == null ? "" : Loc(v);
                if (string.IsNullOrEmpty(txt)) continue;
                string p = txt.Trim();

                bool isRemove = p.IndexOf("移除", StringComparison.Ordinal) >= 0 || p.IndexOf("删除", StringComparison.Ordinal) >= 0
                             || p.IndexOf("Remove", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isRemove) return "remove";
                if (p.IndexOf("升级", StringComparison.Ordinal) >= 0 || p.IndexOf("Upgrade", StringComparison.OrdinalIgnoreCase) >= 0) return "upgrade";
                if (p.IndexOf("变化", StringComparison.Ordinal) >= 0 || p.IndexOf("变形", StringComparison.Ordinal) >= 0 || p.IndexOf("Transform", StringComparison.OrdinalIgnoreCase) >= 0) return "transform";
                if (p.IndexOf("附魔", StringComparison.Ordinal) >= 0 || p.IndexOf("Enchant", StringComparison.OrdinalIgnoreCase) >= 0) return "enchant";
                if (p.IndexOf("弃", StringComparison.Ordinal) >= 0 || p.IndexOf("Discard", StringComparison.OrdinalIgnoreCase) >= 0) return "discard";
                if (p.IndexOf("消耗", StringComparison.Ordinal) >= 0 || p.IndexOf("Exhaust", StringComparison.OrdinalIgnoreCase) >= 0) return "exhaust";
            }
        }
        catch { }
        return null;
    }

    static void Install(Assembly a)
    {
        try
        {
            _harmony = new Harmony("agentseat");

            // 选牌用途：拦 CardSelectCmd 的各 FromDeckFor* 状态机，用类型名判断用途
            try
            {
                Type csc = a.GetType("MegaCrit.Sts2.Core.Commands.CardSelectCmd", false);
                int nPurpose = 0;
                if (csc != null)
                {
                    foreach (Type nt in csc.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        string nn = nt.Name;
                        if (nn.IndexOf("FromDeckFor", StringComparison.OrdinalIgnoreCase) < 0 &&
                            nn.IndexOf("FromDeckGeneric", StringComparison.OrdinalIgnoreCase) < 0 &&
                            nn.IndexOf("FromSimpleGrid", StringComparison.OrdinalIgnoreCase) < 0 &&
                            nn.IndexOf("FromChooseACard", StringComparison.OrdinalIgnoreCase) < 0 &&
                            nn.IndexOf("FromHand", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        MethodInfo mv = nt.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (mv == null) continue;
                        try
                        {
                            _harmony.Patch(mv, new HarmonyMethod(typeof(AgentSeatEntry).GetMethod("PrefixSelectPurpose", BindingFlags.Static | BindingFlags.NonPublic)), null, null);
                            nPurpose++;
                        }
                        catch { }
                    }
                }
                W("选牌用途钩子已挂: " + nPurpose + " 个入口");
            }
            catch (Exception e) { W("选牌用途钩子失败: " + e.Message); }


            // 选牌
            Type sel = a.GetType("MegaCrit.Sts2.Core.AutoSlay.Helpers.AutoSlayCardSelector", false);
            // 选牌改由自己的 ICardSelector 接管（CardSelectCmd.UseSelector），不再 patch AutoSlay 的选择器
            W("选牌旧钩子：已停用（改用自有 selector）");

            // 出牌
            // 看门狗硬上限 30 秒（readonly 改不了）→ 直接拦掉 Check()，由我们自己的等待兜底
            try
            {
                Type wdT = a.GetType("MegaCrit.Sts2.Core.AutoSlay.Helpers.Watchdog", false);
                int patched = 0;
                StringBuilder sigs = new StringBuilder();
                if (wdT != null)
                {
                    foreach (MethodInfo mi in wdT.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (mi.Name != "Check") continue;
                        sigs.Append(mi.Name).Append('(').Append(mi.GetParameters().Length).Append(") ");
                        try
                        {
                            _harmony.Patch(mi, new HarmonyMethod(typeof(AgentSeatEntry).GetMethod("PrefixWatchdogCheck", BindingFlags.Static | BindingFlags.NonPublic)), null, null);
                            patched++;
                        }
                        catch (Exception ex) { W("单个 Check 补丁失败: " + ex.Message); }
                    }
                }
                if (patched == 0 && wdT != null)
                {
                    StringBuilder all = new StringBuilder();
                    foreach (MethodInfo mi in wdT.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        all.Append(mi.Name).Append(' ');
                    W("看门狗实际方法: " + (all.Length > 200 ? all.ToString(0, 200) : all.ToString()));
                }
                if (patched > 0) W("看门狗已禁用（拦截 " + patched + " 个 Check，签名: " + sigs + "）");
                else W("看门狗 Check 没找到（类型=" + (wdT == null ? "null" : "ok") + "）");
            }
            catch (Exception e) { W("看门狗禁用失败: " + e.Message); }

            DisableCheats(a);

            Type slayerType = a.GetType("MegaCrit.Sts2.Core.AutoSlay.AutoSlayer", false);
            if (slayerType != null)
            {
                MethodInfo mm = slayerType.GetMethod("PlayMainMenuAsync", BindingFlags.NonPublic | BindingFlags.Instance);
                if (mm != null)
                {
                    _harmony.Patch(mm, new HarmonyMethod(typeof(AgentSeatEntry).GetMethod("PrefixMainMenu", BindingFlags.Static | BindingFlags.NonPublic)), null, null);
                    W("开局已接管（按角色文件或问她）");
                }
            }

            Type combatH = a.GetType("MegaCrit.Sts2.Core.AutoSlay.Handlers.Rooms.CombatRoomHandler", false);
            if (combatH != null)
            {
                _harmony.Patch(combatH.GetMethod("HandleAsync", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                    new HarmonyMethod(typeof(AgentSeatEntry).GetMethod("PrefixCombatSkip", BindingFlags.Static | BindingFlags.NonPublic)), null, null);
            }

            Type cr = a.GetType("MegaCrit.Sts2.Core.AutoSlay.Handlers.Screens.CardRewardScreenHandler", false);
            if (cr != null)
            {
                _harmony.Patch(cr.GetMethod("HandleAsync", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                    new HarmonyMethod(typeof(AgentSeatEntry).GetMethod("PrefixCardRewardHandle", BindingFlags.Static | BindingFlags.NonPublic)), null, null);
                W("卡牌奖励改为她来选");
            }

            Type cmd = a.GetType("MegaCrit.Sts2.Core.Commands.CardCmd", false);
            if (cmd != null)
            {
                StringBuilder sigs = new StringBuilder();
                foreach (MethodInfo m in cmd.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.Name != "AutoPlay") continue;
                    sigs.Append(m.Name).Append('(');
                    ParameterInfo[] ps = m.GetParameters();
                    for (int q = 0; q < ps.Length; q++) { if (q > 0) sigs.Append(','); sigs.Append(ps[q].ParameterType.Name); }
                    sigs.Append(")  ");
                }
                W("AutoPlay 重载: " + sigs);
            }
            MethodInfo apm = cmd.GetMethod("AutoPlay", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (apm == null) W("AutoPlay 方法没找到");
            else
            {
                W("补丁目标: " + apm.DeclaringType.FullName + "." + apm.Name + " static=" + apm.IsStatic);
                try
                {
                    _harmony.Patch(apm,
                        new HarmonyMethod(typeof(AgentSeatEntry).GetMethod("PrefixAutoPlay", BindingFlags.Static | BindingFlags.NonPublic)), null, null);
                    W("AutoPlay 补丁: 成功");
                }
                catch (Exception ex) { W("AutoPlay 补丁失败: " + ex.Message); }
                try
                {
                    HarmonyLib.Harmony h = _harmony;
                    Patches info = Harmony.GetPatchInfo(apm);
                    W("反查: prefixes=" + (info == null ? "null" : info.Prefixes.Count.ToString()));
                    if (info != null)
                    {
                        foreach (Patch p in info.Prefixes) W("  前缀来自: " + p.owner + " / " + p.PatchMethod.Name);
                    }
                }
                catch (Exception ex2) { W("反查失败: " + ex2.Message); }
            }

            // 商店
            Type shop = a.GetType("MegaCrit.Sts2.Core.AutoSlay.Handlers.Rooms.ShopRoomHandler", false);
            if (shop != null)
            {
                _harmony.Patch(shop.GetMethod("HandleAsync", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                    new HarmonyMethod(typeof(AgentSeatEntry).GetMethod("PrefixShopHandle", BindingFlags.Static | BindingFlags.NonPublic)), null, null);
                W("商店接管已挂");
            }
            else W("找不到 ShopRoomHandler");

            // 选项变化等待：5 秒 -> 45 秒
            Type wh2 = a.GetType("MegaCrit.Sts2.Core.AutoSlay.Helpers.WaitHelper", false);
            if (wh2 != null)
            {
                MethodInfo until = wh2.GetMethod("Until", new Type[] { typeof(Func<bool>), typeof(CancellationToken), typeof(TimeSpan?), typeof(Func<string>) });
                if (until != null)
                {
                    _harmony.Patch(until, new HarmonyMethod(typeof(AgentSeatEntry).GetMethod("PrefixUntil", BindingFlags.Static | BindingFlags.NonPublic)), null, null);
                    W("选项等待已延长");
                }
                else W("找不到 Until 签名");
            }

            // 整局超时：25 分钟 -> 3 小时
            Type wh = a.GetType("MegaCrit.Sts2.Core.AutoSlay.Helpers.WaitHelper", false);
            if (wh != null)
            {
                MethodInfo wt = wh.GetMethod("WithTimeout", new Type[] { typeof(Func<CancellationToken, Task>), typeof(TimeSpan), typeof(CancellationToken) });
                if (wt != null)
                {
                    _harmony.Patch(wt, new HarmonyMethod(typeof(AgentSeatEntry).GetMethod("PrefixWithTimeout", BindingFlags.Static | BindingFlags.NonPublic)), null, null);
                    W("整局超时已接管");
                }
                else W("找不到 WithTimeout 签名");
            }

            W("已安装（选牌 + 出牌 + 商店 + 长超时）");
        try { RelaxHandlerTimeouts(a); } catch (Exception ex) { W("放宽超时失败: " + ex.Message); }
        }
        catch (Exception e) { W("安装失败: " + e); }
    }

    public static void Initialize()
    {
        try { File.WriteAllText(Out, "", new UTF8Encoding(false)); } catch { }
        SceneTree t = Engine.GetMainLoop() as SceneTree;
        if (t != null) t.ProcessFrame += OnFrame;
    }

    // 主循环崩了但游戏还活着 → 自动重启（最多 2 次）
    // 把我们的选牌处理器注册进 AutoSlay 的 _screenHandlers
    static void RegisterLlmHandlers(object slayer)
    {
        if (slayer == null) return;
        try
        {
            object dictObj = Field(slayer, "_screenHandlers");
            System.Collections.IDictionary dict = dictObj as System.Collections.IDictionary;
            if (dict == null) { W("找不到 _screenHandlers"); return; }

            string[,] want = new string[,] {
                { "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckUpgradeSelectScreen",   "upgrade"   },
                { "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckEnchantSelectScreen",   "enchant"   },
                { "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckTransformSelectScreen", "transform" },
                { "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckCardSelectScreen",      "remove"    },
                { "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NSimpleCardSelectScreen",    "reward"    },
                { "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseACardSelectionScreen","reward"    }
            };
            int n = 0;
            for (int i = 0; i < want.GetLength(0); i++)
            {
                Type st = _sts2.GetType(want[i, 0], false);
                if (st == null) { W("注册跳过（类型未找到）: " + want[i, 0]); continue; }
                n++;
            }
            if (n > 0 && !_llmHandlerLogged) { _llmHandlerLogged = true; W("已注册模型选牌处理器: " + n + " 个（AutoSlay 会等模型决策）"); }
        }
        catch (Exception e) { W("注册处理器失败: " + e.Message); }
    }



    // 玩家身上某个能力的层数（按中文名或 Id 关键字匹配）
    // 回合结束的毒伤：毒每次触发掉「当前层数」并 -1；触媒每层多触发一次
    //   触发次数 n = 1 + 触媒；总伤 = n*p - n(n-1)/2，且触发次数不超过毒层数
    // 取一张牌的全部动态变量（Name → IntValue）
    static Dictionary<string, int> VarsDict(object card)
    {
        Dictionary<string, int> d = new Dictionary<string, int>();
        try
        {
            object dv = Prop(card, "DynamicVars");
            if (dv == null) return d;
            System.Collections.IEnumerable vals = Prop(dv, "Values") as System.Collections.IEnumerable;
            if (vals == null) return d;
            foreach (object v in vals)
            {
                if (v == null) continue;
                string nm = Convert.ToString(Prop(v, "Name"));
                if (string.IsNullOrEmpty(nm)) continue;
                d[nm] = I(v, "IntValue");
            }
        }
        catch { }
        return d;
    }

    static int PoisonEndTurnDamage(int poison, int catalyst)
    {
        if (poison <= 0) return 0;
        int n = 1 + Math.Max(0, catalyst);
        if (n > poison) n = poison;
        int total = n * poison - n * (n - 1) / 2;
        return Math.Max(0, total);
    }
    
    static int _endGuardTurn = -1;   // 本回合已经拦过一次

    static int HasRelicOrPower(object player, string cn, string idPart)
    {
        int v = 0;
        try
        {
            object cre = player == null ? null : Prop(player, "Creature");
            IEnumerable seq = cre == null ? null : ((Prop(cre, "Powers") ?? Field(cre, "_powers")) as IEnumerable);
            if (seq == null) return 0;
            foreach (object pw in seq)
            {
                if (pw == null) continue;
                string nm = Loc2(Prop(pw, "Title"));
                string id = S(Prop(pw, "Id"));
                bool hit = (!string.IsNullOrEmpty(nm) && nm.IndexOf(cn, StringComparison.Ordinal) >= 0)
                        || (!string.IsNullOrEmpty(id) && id.IndexOf(idPart, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit) v += I(pw, "Amount");
            }
        }
        catch { }
        return v;
    }
    
    // 商店：每件商品失败次数（连续失败 2 次就跳过，防止死循环）
    static readonly Dictionary<int, int> _shopFail = new Dictionary<int, int>();

    // 玩家有没有某个遗物（按 Id 子串匹配，如 "ICE_CREAM" / "MINIATURE_TENT"）
    public static bool HasRelic(string idPart)
    {
        bool has = false;
        RunOnMain(new Action(() =>
        {
            try
            {
                object player = Player0();
                if (player == null) return;
                IEnumerable rs = Prop(player, "Relics") as IEnumerable;
                if (rs == null) return;
                foreach (object r in rs)
                {
                    if (r == null) continue;
                    string id = S(Prop(r, "Id"));
                    if (!string.IsNullOrEmpty(id) && id.IndexOf(idPart, StringComparison.OrdinalIgnoreCase) >= 0) { has = true; return; }
                }
            }
            catch { }
        }));
        return has;
    }

    // 本回合"她说够了"被拒了几次（换回合清零）

    // ================= 放宽 AutoSlay 的超时上限 =================
    // 证据：AutoSlayer.HandleRoomAsync → WaitHelper.WithTimeout(handler.HandleAsync, handler.Timeout, ct)
    // 我们的模式"每张牌问一次模型"，一场战斗轻松超过 5 分钟 → 必须放宽 handler.Timeout
    static bool PrefixHandlerTimeout(ref TimeSpan __result)
    {
        __result = TimeSpan.FromMinutes(30);
        return false;   // 跳过原实现
    }

    static void RelaxHandlerTimeouts(Assembly a)
    {
        int patched = 0;
        string[] names = new string[] {
            "CombatRoomHandler", "EventRoomHandler", "ShopRoomHandler",
            "RestSiteRoomHandler", "TreasureRoomHandler",
            "CardRewardScreenHandler", "RewardsScreenHandler", "MapScreenHandler"
        };
        for (int i = 0; i < names.Length; i++)
        {
            try
            {
                Type t = a.GetType("MegaCrit.Sts2.Core.AutoSlay.Handlers.Rooms." + names[i], false);
                if (t == null) t = a.GetType("MegaCrit.Sts2.Core.AutoSlay.Handlers.Screens." + names[i], false);
                if (t == null) continue;
                PropertyInfo pi = t.GetProperty("Timeout", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo getter = pi == null ? null : pi.GetGetMethod(true);
                if (getter == null) continue;
                _harmony.Patch(getter, new HarmonyMethod(typeof(AgentSeatEntry).GetMethod("PrefixHandlerTimeout", BindingFlags.Static | BindingFlags.NonPublic)), null, null);
                patched++;
            }
            catch (Exception e) { W("放宽超时失败(" + names[i] + "): " + e.Message); }
        }
        W("已放宽 " + patched + " 个 handler 的超时 → 30 分钟");
    }

    // 90 秒没有推进就告警一次（能看出是"慢"还是"卡死"）
    static DateTime _lastProgress = DateTime.UtcNow;
    static string _lastProgressSig = "";
    static DateTime _lastWarn = DateTime.MinValue;

    static void TickProgressWatch()
    {
        try
        {
            string sig = "";
            RunOnMain(new Action(() =>
            {
                try
                {
                    object state = StateNow();
                    object player = Player0();
                    object pcs = player == null ? null : Prop(player, "PlayerCombatState");
                    sig = (state == null ? "norun" : RoomTypeText(state) ?? "?")
                        + "|" + (pcs == null ? "-" : (PhaseText(pcs) + "/" + I(pcs, "TurnNumber") + "/" + L(Prop(Prop(pcs, "Hand"), "Cards")).Count));
                }
                catch { sig = "err"; }
            }));
            if (sig != _lastProgressSig)
            {
                _lastProgressSig = sig;
                _lastProgress = DateTime.UtcNow;
                return;
            }
            if ((DateTime.UtcNow - _lastProgress).TotalSeconds > 90 && (DateTime.UtcNow - _lastWarn).TotalSeconds > 90)
            {
                _lastWarn = DateTime.UtcNow;
                W("[告警] 90 秒没有推进（" + sig + "）——仍在等待，不是崩溃");
            }
        }
        catch { }
    }

    static void TryRestartLoop()
    {
        try
        {
            if (!_loopRestartPending) return;
            _loopRestartPending = false;
            if (_loopRestarts >= 4) { W("主循环已重启过 4 次，不再重启"); return; }

            bool alive = false;
            RunOnMain(new Action(() => { alive = RunStateNow() != null; }));
            if (!alive) { W("游戏已结束，不重启主循环"); return; }

            Type st = _sts2.GetType("MegaCrit.Sts2.Core.AutoSlay.AutoSlayer", false);
            MethodInfo play = st == null ? null : st.GetMethod("PlayRunAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            if (play == null || _slayerInst == null) { W("找不到 PlayRunAsync，无法重启"); return; }

            _loopRestarts++;
            W("重启主循环（第 " + _loopRestarts + " 次）");
            Task t2 = (Task)play.Invoke(_slayerInst, new object[] { Seed + "-r" + _loopRestarts, CancellationToken.None });
            if (t2 != null)
            {
                t2.ContinueWith(delegate (Task t)
                {
                    if (t.IsFaulted)
                    {
                        string full = t.Exception == null ? "?" : t.Exception.GetBaseException().ToString();
                        W("重启后的循环也异常: " + full.Substring(0, Math.Min(600, full.Length)));
                        _loopRestartPending = true;
                    }
                    else W("重启后的循环正常结束");
                });
            }
        }
        catch (Exception e) { W("重启主循环失败: " + e.Message); }
    }


    // ===== 给 LlmScreenHandler 用的公开桥 =====
    public static void RunOnMainPublic(Action a) { RunOnMain(a); }

    public static string AskPublic(string payload, string tag) { return Ask(payload, tag); }

    // 组一个"选牌"payload（和普通选牌一致，但候选来自 UI 节点）
    public static string BuildPickPayload(List<Node> holders, string purpose)
    {
        StringBuilder sb = new StringBuilder();
        RunOnMain(new Action(() =>
        {
            try
            {
                int id = Interlocked.Increment(ref _seq);
                sb.Append("{\"id\":").Append(id).Append(",\"kind\":\"pick_cards\",\"min\":1,\"max\":")
                  .Append(Math.Max(1, holders.Count))
                  .Append(",\"purpose\":\"").Append(purpose)
                  .Append("\",\"pick\":\"").Append(
                      (purpose == "remove" || purpose == "transform" || purpose == "discard" || purpose == "exhaust")
                          ? "lowest" : "highest")
                  .Append("\",\"options\":[");
                string chId = CharId();
                for (int i = 0; i < holders.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    object card = Prop(holders[i], "CardModel") ?? Prop(holders[i], "Model") ?? Prop(holders[i], "Card");
                    string title = card == null ? "?" : S(Prop(card, "Title"));
                    sb.Append("{\"i\":").Append(i).Append(",\"title\":\"").Append(J(title)).Append('"');
                    CardInfo info = FindCardInfo(title, chId);
                    if (info != null)
                    {
                        int sc;
                        string key = info.Id + "|" + chId;
                        if (_kbScore.TryGetValue(key, out sc))
                        {
                            if (purpose == "upgrade" || purpose == "enchant")
                            {
                                int up;
                                if (!_kbUp.TryGetValue(key, out up) || up < 0)
                                {
                                    double gain = info.EffUp > info.Eff ? (info.EffUp - info.Eff) : 0;
                                    up = (int)Math.Round(sc * 0.35 + gain * 8);
                                }
                                sb.Append(",\"score\":").Append(up);
                            }
                            else sb.Append(",\"score\":").Append(sc);
                            string tier;
                            if (_kbTier.TryGetValue(key, out tier) && !string.IsNullOrEmpty(tier))
                                sb.Append(",\"tier\":\"").Append(tier).Append('"');
                            string kw;
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
            }
            catch (Exception e) { sb.Length = 0; sb.Append("{\"kind\":\"pick_cards\",\"purpose\":\"").Append(purpose).Append("\",\"options\":[]}"); W("组选牌payload失败: " + e.Message); }
        }));
        return sb.ToString();
    }

    public static List<int> ParsePicks(string resp)
    {
        List<int> picks = new List<int>();
        try
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(resp ?? "", "\"picks\"\\s*:\\s*\\[([^\\]]*)\\]");
            if (!m.Success) return picks;
            string[] parts = m.Groups[1].Value.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                int v;
                if (int.TryParse(parts[i].Trim(), out v) && !picks.Contains(v)) picks.Add(v);
            }
        }
        catch { }
        return picks;
    }

    // 模型没回时的本地兜底（按分数），保证不卡死
    public static List<int> FallbackPicks(List<Node> holders, string purpose, int need)
    {
        List<int> idx = new List<int>();
        List<int> scores = new List<int>();
        for (int i = 0; i < holders.Count; i++) { idx.Add(i); scores.Add(ScoreOfHolder(holders[i], purpose)); }
        bool low = (purpose == "remove" || purpose == "transform" || purpose == "discard" || purpose == "exhaust");
        idx.Sort(delegate (int a, int b) { return low ? scores[a].CompareTo(scores[b]) : scores[b].CompareTo(scores[a]); });
        List<int> res = new List<int>();
        for (int i = 0; i < idx.Count && res.Count < need; i++) res.Add(idx[i]);
        return res;
    }

    // ===== 供 SelectionDriver 调用的桥接方法 =====
    public static void DrvLog(string msg)
    {
        W("[选牌接管] " + msg);
    }

    // 找到某个类型名的"可见"节点（不要求 IsVisibleInTree 为真时也接受）
    public static object FindVisibleNode(string typeName)
    {
        object found = null;
        RunOnMain(new Action(() =>
        {
            try
            {
                SceneTree tree = Engine.GetMainLoop() as SceneTree;
                if (tree == null) return;
                List<Node> hits = new List<Node>();
                CollectByTypeName(tree.Root, typeName, hits, 0);
                for (int i = 0; i < hits.Count; i++)
                {
                    CanvasItem ci = hits[i] as CanvasItem;
                    if (ci == null || ci.IsVisibleInTree()) { found = hits[i]; return; }
                }
                if (hits.Count > 0) found = hits[0];
            }
            catch { }
        }));
        return found;
    }

    public static void CollectNodesOfType(Node root, string[] typeNames, List<Node> acc)
    {
        RunOnMain(new Action(() =>
        {
            try
            {
                for (int i = 0; i < typeNames.Length; i++)
                    CollectByTypeName(root, typeNames[i], acc, 0);
            }
            catch { }
        }));
    }

    public static string TitleOfHolder(Node holder)
    {
        string title = "";
        RunOnMain(new Action(() =>
        {
            try
            {
                object card = Prop(holder, "CardModel") ?? Prop(holder, "Model") ?? Prop(holder, "Card");
                if (card != null) title = S(Prop(card, "Title"));
            }
            catch { }
        }));
        return title;
    }

    // 分数：upgrade/enchant 用升级分，其余用卡牌分
    public static int ScoreOfHolder(Node holder, string purpose)
    {
        int score = 0;
        RunOnMain(new Action(() =>
        {
            try
            {
                object card = Prop(holder, "CardModel") ?? Prop(holder, "Model") ?? Prop(holder, "Card");
                if (card == null) return;
                string title = S(Prop(card, "Title"));
                CardInfo info = FindCardInfo(title, CharId());
                if (info == null) return;
                string key = info.Id + "|" + CharId();
                int baseScore;
                if (!_kbScore.TryGetValue(key, out baseScore)) baseScore = 40;
                if (purpose == "upgrade" || purpose == "enchant")
                {
                    int up;
                    if (_kbUp.TryGetValue(key, out up) && up >= 0) { score = up; return; }
                    double gain = info.EffUp > info.Eff ? (info.EffUp - info.Eff) : 0;
                    score = (int)Math.Round(baseScore * 0.35 + gain * 8);
                    return;
                }
                score = baseScore;
            }
            catch { }
        }));
        return score;
    }

    // 这个屏要选几张（读不到就当 1）
    public static int RequiredPickCount(object screen)
    {
        int n = 1;
        RunOnMain(new Action(() =>
        {
            try
            {
                object pref = Prop(screen, "Prefs") ?? Field(screen, "_prefs");
                if (pref == null) return;
                object mn = Prop(pref, "Min") ?? Prop(pref, "MinSelect") ?? Prop(pref, "MinCount");
                if (mn != null) n = Math.Max(1, Convert.ToInt32(mn));
            }
            catch { }
        }));
        return n;
    }

    public static Node FindConfirmButton(object screen)
    {
        Node found = null;
        RunOnMain(new Action(() =>
        {
            try
            {
                Node root = screen as Node;
                if (root == null) return;
                List<Node> hits = new List<Node>();
                CollectByTypeName(root, "NConfirmButton", hits, 0);
                if (hits.Count > 0) { found = hits[0]; return; }
                CollectByTypeName(root, "NProceedButton", hits, 0);
                if (hits.Count > 0) found = hits[0];
            }
            catch { }
        }));
        return found;
    }

    public static void ClickNode(Node n)
    {
        if (n == null) return;
        try { SelectionDriverClick(n); } catch { }
    }

    static void SelectionDriverClick(Node n)
    {
        RunOnMain(new Action(() =>
        {
            try
            {
                // 优先走节点自己的 ForceClick（NClickableControl）
                MethodInfo fc = n.GetType().GetMethod("ForceClick", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fc != null)
                {
                    fc.Invoke(n, fc.GetParameters().Length == 0 ? null : new object[] { false });
                    return;
                }
                // 退回发 Pressed 信号（用 GodotObject.EmitSignal 的重载）
                MethodInfo[] ms = n.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                foreach (MethodInfo m2 in ms)
                {
                    if (m2.Name != "EmitSignal") continue;
                    ParameterInfo[] ps = m2.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(Godot.Variant[]))
                    {
                        Godot.Collections.Array arr = new Godot.Collections.Array();
                        arr.Add(n);
                        Godot.Variant[] va = new Godot.Variant[] { Godot.Variant.CreateFrom(n) };
                        m2.Invoke(n, new object[] { "Pressed", va });
                        return;
                    }
                }
            }
            catch { }
        }));
    }

    static void OnFrame()
    {
        if (_mainThreadId == 0) _mainThreadId = Thread.CurrentThread.ManagedThreadId;

        Action act;
        int drained = 0;
        while (drained < 8 && _mainQ.TryDequeue(out act))
        {
            try { act(); } catch (Exception e) { W("主线程队列异常: " + e.Message); }
            drained++;
        }

        _frames++;
        if (_frames % 15 == 0 && _mapPick == null) { try { PrefetchMap(); } catch { } }
        if (_loopRestartPending && _frames % 60 == 0) { TryRestartLoop(); }
        if (_frames % 60 == 0) { try { TickSelector(); } catch { } }
        if (_frames % 6 == 0) { try { TickHandWatch(); } catch { } }
        if (_frames % 120 == 0) { try { TickProgressWatch(); } catch { } }
        if (_frames % 120 == 0) { try { TickSandpit(); } catch { } }   // 手动模式也能看沙坑层数
        if (_frames % 120 == 0) { try { TickHandProbe(); } catch { } }
        if (_frames % 120 == 0) { try { TickIntentProbe(); } catch { } }
        if (_frames % 120 == 0) { try { TickPlayProbe(); } catch { } }  // 手动模式也能看可打出
        if (_frames % 120 == 0) { try { TickOptionProbe(); } catch { } } // 选项节点禁用字段探针
        if (_frames % 120 == 0) { try { TickBundleProbe(); } catch { } } // 卡包界面探针
        if (_frames % 120 == 0) { try { TickScreenProbe(); } catch { } } // 列出可见屏幕类型
        if (_frames % 120 == 0) { try { TickGameOver(); } catch { } }  // 结算界面（失败/通关）
        Assembly a = FindSts2();
        if (a == null) return;
        if (!_installed) { _installed = true; Install(a); }
        if (_started || _frames < 30) return;
        try
        {
            Type gt = a.GetType("MegaCrit.Sts2.Core.Nodes.NGame");
            object game = gt.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
            if (game == null) return;
            object done = gt.GetProperty("GameStartupComplete", BindingFlags.Public | BindingFlags.Instance).GetValue(game, null);
            bool ready = (done is bool) ? (bool)done : (done is Task && ((Task)done).IsCompleted);
            if (!ready) return;
            if (gt.GetProperty("MainMenu", BindingFlags.Public | BindingFlags.Instance).GetValue(game, null) == null) return;
            _started = true;
            Type st = a.GetType("MegaCrit.Sts2.Core.AutoSlay.AutoSlayer");
            object sl = Activator.CreateInstance(st);
            if (!AgentMode())
            {
                W("手动模式，不启动 AutoSlay");
                return;
            }

            // 方案 B：直接调 AutoSlayer 的私有 PlayRunAsync —— 用它的完整循环，
            // 但绕开 RunAsync 的 25 分钟封顶和结束时的 QuitGame。
            // 初始化它的日志，否则看不到房间流水
            try
            {
                Type lg = _sts2.GetType("MegaCrit.Sts2.Core.AutoSlay.AutoSlayLog", false);
                MethodInfo open = lg == null ? null : lg.GetMethod("OpenLogFile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (open != null) { open.Invoke(null, new object[] { AutoSlayLog }); W("AutoSlay 日志已初始化"); }
            }
            catch (Exception e) { W("日志初始化失败: " + e.Message); }

            MethodInfo play = st.GetMethod("PlayRunAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            if (play != null)
            {
                _slayerInst = sl;
                RegisterLlmHandlers(sl);
                Task loopTask = (Task)play.Invoke(sl, new object[] { Seed, CancellationToken.None });
                W("已启动 PlayRunAsync（绕过封顶与退出）");
                if (loopTask != null)
                {
                    TaskCompletes();
                    loopTask.ContinueWith(delegate (Task t)
                    {
                        if (!t.IsFaulted)
                        {
                            try
                            {
                                int fl = 0; string ch = "";
                                RunOnMain(new Action(() =>
                                {
                                    object rs = RunStateNow();
                                    if (rs != null) { fl = FloorOf(rs); ch = CharId(); }
                                }));
                                PostRunEnd(fl, fl >= 49 ? "win" : "end", ch);
                            }
                            catch { }
                        }
                        if (t.IsFaulted)
                        {
                            string full = t.Exception == null ? "?" : t.Exception.GetBaseException().ToString();
                            W("循环结束（异常）: " + (t.Exception != null ? t.Exception.GetBaseException().Message : "?"));
                            W("异常详情: " + full.Substring(0, Math.Min(900, full.Length)));
                            _loopRestartPending = true;    // 交给 OnFrame 自动重启
                        }
                        else W("循环结束（正常）");
                    });
                }
            }
            else
            {
                W("找不到 PlayRunAsync，退回 Start");
                st.GetMethod("Start", new Type[] { typeof(string), typeof(string) }).Invoke(sl, new object[] { Seed, AutoSlayLog });
            }

            // AutoSlay 会把游戏设成"快进"（FastModeType.Fast），关掉它让敌人出招可见
            Task.Run(new Action(delegate
            {
                try
                {
                    Thread.Sleep(1500);
                    Type sm = null;
                    foreach (Type tt in FindSts2().GetTypes()) { if (tt != null && tt.Name == "SaveManager") { sm = tt; break; } }
                    if (sm == null) { W("找不到 SaveManager"); return; }
                    object inst = sm.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                    object prefs = Prop(inst, "PrefsSave");
                    if (prefs == null) { W("PrefsSave 为空"); return; }
                    PropertyInfo fp = prefs.GetType().GetProperty("FastMode", BindingFlags.Public | BindingFlags.Instance);
                    if (fp == null) { W("找不到 FastMode"); return; }
                    Type et = fp.PropertyType;
                    object target = null;
                    StringBuilder names = new StringBuilder();
                    foreach (string n in Enum.GetNames(et)) { names.Append(n).Append(','); if (n == "Fast") { target = Enum.Parse(et, n); } }
                    W("FastMode 可选: " + names);
                    if (target != null) { fp.SetValue(prefs, target, null); W("FastMode -> " + target); }
                }
                catch (Exception e) { W("FastMode 设置失败: " + e.Message); }
            }));

            W("AutoSlay 已启动");
        }
        catch (Exception e) { W("启动失败: " + e.Message); }
    }
}





