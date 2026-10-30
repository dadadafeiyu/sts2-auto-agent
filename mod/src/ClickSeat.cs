using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Modding;

// 只做一件事：拦住地图点的点击，换成她选的那一个（不阻塞主线程）
// [ModInitializer] 由 SuiteEntry 统一调用
public static class ClickSeatEntry
{
    static readonly string Out = Paths.Logs + @"\clickseat.log";
    static readonly string AutoSlayLog = Paths.Logs + @"\autoslay.log";
    const string Base = "http://127.0.0.1:18731";

    static readonly System.Net.Http.HttpClient Http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    static readonly ConcurrentQueue<Action> _mainQ = new ConcurrentQueue<Action>();
    static bool _evSigLogged;
    static bool _chainLogged;
    static Assembly _sts2;
    static int _frames, _mainThreadId, _seq;
    static bool _installed, _started, _asking;

    [ThreadStatic] static bool _inFallback;

    static void W(string s) { try { File.AppendAllText(Out, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\r\n", new UTF8Encoding(false)); } catch { } }

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
    static string J(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");
    }
    static void RunOnMain(Action a)
    {
        if (Thread.CurrentThread.ManagedThreadId == _mainThreadId) { a(); return; }
        ManualResetEventSlim done = new ManualResetEventSlim(false);
        _mainQ.Enqueue(new Action(() => { try { a(); } catch (Exception e) { W("主线程异常 " + e.Message); } finally { done.Set(); } }));
        done.Wait(15000);
    }

    static NMapScreen MapScreen()
    {
        Type mt = _sts2.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen", false);
        PropertyInfo inst = mt.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        return inst == null ? null : inst.GetValue(null, null) as NMapScreen;
    }

    static bool IsMapPoint(object node)
    {
        if (node == null) return false;
        string n = node.GetType().Name;
        return n == "NNormalMapPoint" || n == "NBossMapPoint" || n == "NAncientMapPoint";
    }

    static string BuildNodesJson(NMapScreen screen, List<NMapPoint> travel, List<string> coords, List<object> models)
    {
        LoadRouteRules();
        int hpPct2 = HpPercent();
        int monstersSoFar = MonstersThisAct(screen);
        int id = Interlocked.Increment(ref _seq);
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"id\":").Append(id).Append(",\"kind\":\"pick_map\",\"nodes\":[");
        for (int i = 0; i < travel.Count; i++)
        {
            if (i > 0) sb.Append(',');
            object m = models[i];
            string ty = S(Prop(m, "PointType") ?? Prop(m, "Type") ?? Prop(m, "RoomType"));
            int sc; string wy;
            RouteScore(ty, hpPct2, monstersSoFar, out sc, out wy);
            sb.Append("{\"i\":").Append(i).Append(",\"coord\":\"").Append(J(coords[i])).Append('"')
              .Append(",\"type\":\"").Append(J(ty)).Append('"')
              .Append(",\"score\":").Append(sc)
              .Append(",\"why\":\"").Append(J(wy)).Append("\"}");
        }
        sb.Append("]}");
        // 诊断：确认"一幕内小怪计数"是否生效
        try
        {
            StringBuilder dbg = new StringBuilder();
            for (int i = 0; i < travel.Count; i++)
            {
                object m2 = models[i];
                string ty2 = S(Prop(m2, "PointType") ?? Prop(m2, "Type") ?? Prop(m2, "RoomType"));
                int sc2; string wy2;
                RouteScore(ty2, hpPct2, monstersSoFar, out sc2, out wy2);
                if (i > 0) dbg.Append(" / ");
                dbg.Append(BaseType(ty2)).Append('(').Append(sc2).Append(')');
            }
            W("路线: 小怪计数=" + monstersSoFar + " 血量=" + hpPct2 + "% 候选: " + dbg);
        }
        catch { }
        return sb.ToString();
    }

    static string Post(string json)
    {
        StringContent c = new StringContent(json, Encoding.UTF8, "application/json");
        HttpResponseMessage r = Http.PostAsync(Base + "/decide", c).GetAwaiter().GetResult();
        string body = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!r.IsSuccessStatusCode) throw new Exception("HTTP " + (int)r.StatusCode + ": " + body);
        return body;
    }

    static string LabelOf(Node n, int depth)
    {
        if (n == null || depth > 6) return "";
        try
        {
            Type t = n.GetType();
            if (t.Name == "Label" || t.Name == "RichTextLabel")
            {
                PropertyInfo p = t.GetProperty("Text", BindingFlags.Public | BindingFlags.Instance);
                if (p != null)
                {
                    object v = p.GetValue(n, null);
                    string s = v == null ? "" : Convert.ToString(v);
                    if (s.Length > 0) return s;
                }
            }
        }
        catch { }
        int c = 0;
        try { c = n.GetChildCount(); } catch { return ""; }
        for (int i = 0; i < c; i++)
        {
            Node ch = null;
            try { ch = n.GetChild(i); } catch { }
            string r = LabelOf(ch, depth + 1);
            if (r.Length > 0) return r;
        }
        return "";
    }

    static List<Node> Siblings(object node)
    {
        List<Node> list = new List<Node>();
        Node n = node as Node;
        if (n == null) return list;
        Node parent = null;
        try { parent = n.GetParent(); } catch { }
        if (parent == null) { list.Add(n); return list; }
        string tn = n.GetType().Name;
        int c = 0;
        try { c = parent.GetChildCount(); } catch { return list; }
        for (int i = 0; i < c; i++)
        {
            Node ch = null;
            try { ch = parent.GetChild(i); } catch { }
            if (ch != null && ch.GetType().Name == tn) list.Add(ch);
        }
        if (list.Count == 0) list.Add(n);
        return list;
    }

    // 选项文字：优先 Option.Title（本地化），否则退回节点里的 Label
    // 当前事件模型（EventRoom.LocalMutableEvent）
    static object CurrentEventModel()
    {
        try
        {
            Type rm = _sts2.GetType("MegaCrit.Sts2.Core.Runs.RunManager", false);
            object rmi = rm.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
            object st = Prop(rmi, "State") ?? Field(rmi, "<State>k__BackingField");
            object room = st == null ? null : Prop(st, "CurrentRoom");
            object ev = room == null ? null : Prop(room, "LocalMutableEvent");
            if (!_chainLogged)
            {
                _chainLogged = true;
                W("事件链: rmi=" + (rmi == null ? "null" : "ok") +
                  " state=" + (st == null ? "null" : st.GetType().Name) +
                  " room=" + (room == null ? "null" : room.GetType().Name) +
                  " event=" + (ev == null ? "null" : ev.GetType().Name));
            }
            return ev;
        }
        catch (Exception e) { W("事件链异常: " + e.Message); return null; }
    }

    // 事件选项的描述（GetOptionDescription(index)，拿不到就退回 Option.Description）
    static string EventOptionDesc(object ev, int index, Node node)
    {
        object opt = null;
        try { opt = Prop(node, "Option"); } catch { }
        if (ev != null && opt != null)
        {
            string[] keys = new string[] { "Key", "Id", "OptionKey", "TitleKey", "Name", "LocKey" };
            for (int i = 0; i < keys.Length; i++)
            {
                string k = null;
                try { k = Convert.ToString(Prop(opt, keys[i])); } catch { }
                if (string.IsNullOrEmpty(k)) continue;
                foreach (MethodInfo m in ev.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "GetOptionDescription") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1 || ps[0].ParameterType != typeof(string)) continue;
                    try
                    {
                        string t = LocText(m.Invoke(ev, new object[] { k }));
                        if (!string.IsNullOrEmpty(t)) return t;
                    }
                    catch { }
                }
            }
        }
        try
        {
            if (opt != null)
            {
                string d = LocText(Prop(opt, "Description"));
                if (!string.IsNullOrEmpty(d)) return d;
            }
        }
        catch { }
        return "";
    }

    static string OptionText(Node node, int index)
    {
        try
        {
            object option = Prop(node, "Option");
            if (option != null)
            {
                object title = Prop(option, "Title");
                string t = LocText(title);
                if (!string.IsNullOrEmpty(t)) return t;
                object desc = Prop(option, "Description");
                string d = LocText(desc);
                if (!string.IsNullOrEmpty(d)) return d;
            }
        }
        catch { }
        return "";
    }

    static string LocText(object o)
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

    // 火堆建议：按血量 + 卡组质变牌决定"睡觉"还是"锻造"
    static void RestAdvice(out int sleepScore, out int smithScore, out string why)
    {
        int hp = AgentSeatEntry.HpPctNow();
        List<string> deck = AgentSeatEntry.DeckTitles();
        string worthyCard = null, worthyNote = null;
        for (int i = 0; i < deck.Count; i++)
        {
            string note;
            if (AgentSeatEntry.UpgradeWorthy(deck[i], out note)) { worthyCard = deck[i]; worthyNote = note; break; }
        }

        if (hp < 25)
        {
            sleepScore = 85; smithScore = 15;
            why = "血量 " + hp + "% < 25%，优先休息";
        }
        else if (hp < 50)
        {
            // 25%~50%：睡觉和锻造同分，交给她自己权衡（不再由我们硬压一边）
            if (worthyCard == null) { sleepScore = 50; smithScore = 50; why = "血量 " + hp + "%，同分：没有特别值得升级的牌，睡或锻都行"; }
            else { sleepScore = 50; smithScore = 50; why = "血量 " + hp + "%，同分：可升级「" + worthyCard + "」(" + worthyNote + ")，睡或锻都行"; }
        }
        else
        {
            sleepScore = 25; smithScore = 75;
            if (worthyCard != null) why = "血量 " + hp + "%，建议锻造「" + worthyCard + "」(" + worthyNote + ")";
            else why = "血量 " + hp + "%，可以锻造任意非打击/防御牌";
        }
    }

    static bool HandleOption(object node)
    {
        if (AgentSeatEntry.RunEndedNow()) return false;
        if (_asking)
        {
            // 上一拍还没结束：不吞掉这次点击，排队等它释放后再处理
            W("选项拦截忙，排队重试: " + node.GetType().Name);
            Task.Run(new Action(() =>
            {
                for (int i = 0; i < 60; i++)
                {
                    Thread.Sleep(200);
                    if (!_asking) break;
                }
                if (!_asking) HandleOption(node);
                else W("选项排队超时，放弃该次点击");
            }));
            return false;
        }
        _asking = true;
        Task.Run(new Action(() =>
        {
            try
            {
                List<Node> opts = new List<Node>();
                List<string> labels = new List<string>();
                RunOnMain(new Action(() =>
                {
                    List<Node> sib = Siblings(node);
                    object evModel = CurrentEventModel();
                    if (!_evSigLogged && evModel != null)
                    {
                        _evSigLogged = true;
                        StringBuilder sig = new StringBuilder();
                        foreach (MethodInfo mi in evModel.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (mi.Name.IndexOf("Option", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            sig.Append(mi.Name).Append('(');
                            ParameterInfo[] ps = mi.GetParameters();
                            for (int q = 0; q < ps.Length; q++) { if (q > 0) sig.Append(','); sig.Append(ps[q].ParameterType.Name); }
                            sig.Append(")->").Append(mi.ReturnType.Name).Append("  ");
                        }
                        W("事件模型方法: " + (sig.Length > 600 ? sig.ToString(0, 600) : sig.ToString()));
                    }


                    for (int i = 0; i < sib.Count; i++)
                    {
                        if (IsNodeDisabled(sib[i])) { W("跳过禁用选项: " + sib[i].GetType().Name); continue; }
                        opts.Add(sib[i]);
                        string lbl = "";
                        if (sib[i].GetType().Name == "NEventOptionButton")
                        {
                            string title = OptionText(sib[i], i);
                            string desp = EventOptionDesc(evModel, i, sib[i]);
                            lbl = string.IsNullOrEmpty(desp) ? title : (title + "：" + desp);
                        }
                        else lbl = OptionText(sib[i], i);
                        if (string.IsNullOrEmpty(lbl)) lbl = LabelOf(sib[i], 0);
                        if (string.IsNullOrEmpty(lbl) && sib[i].GetType().Name == "NRestSiteButton")
                            lbl = (i == 0) ? "休息（回复生命）" : "锻造（升级一张牌）";
                        if (string.IsNullOrEmpty(lbl)) lbl = "选项" + i;
                        labels.Add(lbl);
                    }
                }));
                if (opts.Count <= 1) { RunOnMain(new Action(() => ForceClickNode(node))); return; }

                int[] labelsWithScore = null;
                string[] labelsWhy = null;
                if (opts.Count > 0 && opts[0].GetType().Name == "NRestSiteButton")
                {
                    int sleepSc, smithSc; string rw;
                    RestAdvice(out sleepSc, out smithSc, out rw);
                    labelsWithScore = new int[opts.Count];
                    labelsWhy = new string[opts.Count];
                    for (int q = 0; q < opts.Count; q++)
                    {
                        bool isSmith = labels[q].IndexOf("锻造") >= 0;
                        labelsWithScore[q] = isSmith ? smithSc : sleepSc;
                        labelsWhy[q] = rw;
                    }
                    W("火堆建议: " + rw + "（睡 " + sleepSc + " / 锻 " + smithSc + "）");
                }

                int id = Interlocked.Increment(ref _seq);
                StringBuilder sb = new StringBuilder();
                object evm = CurrentEventModel();
                string evTitle = LocText(Prop(evm, "Title"));
                string evDesc = LocText(Prop(evm, "Description"));
                sb.Append("{\"id\":").Append(id).Append(",\"kind\":\"pick_option\"");
                if (!string.IsNullOrEmpty(evTitle)) sb.Append(",\"event\":\"").Append(J(evTitle)).Append('"');
                if (!string.IsNullOrEmpty(evDesc)) sb.Append(",\"eventDesc\":\"").Append(J(evDesc)).Append('"');
                sb.Append(",\"options\":[");
                for (int i = 0; i < opts.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"i\":").Append(i).Append(",\"label\":\"").Append(J(labels[i])).Append('"');
                    if (labelsWithScore != null && labelsWithScore.Length > i)
                        sb.Append(",\"score\":").Append(labelsWithScore[i]).Append(",\"why\":\"").Append(J(labelsWhy != null && labelsWhy.Length > i ? labelsWhy[i] : "")).Append('"');
                    sb.Append('}');
                }
                sb.Append("]}");
                string resp = Post(sb.ToString());
                Match m = Regex.Match(resp ?? "", "\"index\"\\s*:\\s*(-?\\d+)");
                int pick = m.Success ? int.Parse(m.Groups[1].Value) : -1;
                if (pick < 0 || pick >= opts.Count) pick = 0;
                W("选项 -> 她要第 " + pick + " 个（" + opts.Count + " 选 1）：" + labels[pick]);
                Node chosen = opts[pick];
                RunOnMain(new Action(() => ForceClickNode(chosen)));
                // 有「微型帐篷」时火堆能选两项：等第一个生效后再问一次
                try
                {
                    if (chosen != null && chosen.GetType().Name == "NRestSiteButton" && AgentSeatEntry.HasRelic("MINIATURE_TENT"))
                    {
                        Thread.Sleep(1200);
                        List<Node> more = new List<Node>();
                        List<string> moreLabels = new List<string>();
                        RunOnMain(new Action(() =>
                        {
                            List<Node> sib2 = Siblings(node);
                            for (int q = 0; q < sib2.Count; q++)
                            {
                                Node b = sib2[q];
                                if (b == null || !(b is CanvasItem)) continue;
                                CanvasItem ci2 = b as CanvasItem;
                                try { if (!ci2.IsVisibleInTree()) continue; } catch { }
                                object en = null;
                                try { en = Prop(b, "Option"); en = en == null ? null : Prop(en, "IsEnabled"); } catch { }
                                if (en is bool && !(bool)en) continue;
                                more.Add(b);
                                moreLabels.Add(b.GetType().Name == "NRestSiteButton" ? (more.Count == 1 ? "第二项A" : "第二项B") : "选项");
                            }
                        }));
                        if (more.Count > 1)
                        {
                            int id2 = Interlocked.Increment(ref _seq);
                            StringBuilder sb2 = new StringBuilder();
                            sb2.Append("{\"id\":").Append(id2).Append(",\"kind\":\"pick_option\",\"event\":\"营火（帐篷第二项）\",\"options\":[");
                            for (int q = 0; q < more.Count; q++)
                            {
                                if (q > 0) sb2.Append(',');
                                sb2.Append("{\"i\":").Append(q).Append(",\"label\":\"").Append(J(moreLabels[q])).Append("\"}");
                            }
                            sb2.Append("]}");
                            string resp2 = Post(sb2.ToString());
                            Match m2 = Regex.Match(resp2 ?? "", "\"index\"\\s*:\\s*(-?\\d+)");
                            int pick2 = m2.Success ? int.Parse(m2.Groups[1].Value) : 0;
                            if (pick2 < 0 || pick2 >= more.Count) pick2 = 0;
                            W("火堆第二项（帐篷）→ 第 " + pick2 + " 个");
                            Node chosen2 = more[pick2];
                            RunOnMain(new Action(() => ForceClickNode(chosen2)));
                        }
                    }
                }
                catch (Exception e2) { W("火堆第二项失败: " + e2.Message); }
            }
            catch (Exception e) { W("选项拦截异常(" + node.GetType().Name + "): " + e.Message); RunOnMain(new Action(() => ForceClickNode(node))); }
            finally { _asking = false; }
        }));
        return false;
    }

    static bool PrefixUiClick(object[] __args)
    {
        if (!AgentMode()) return true;
        if (_inFallback) return true;
        object node = __args != null && __args.Length > 0 ? __args[0] : null;
        if (node == null) return true;
        string tn = node.GetType().Name;
        return true;   // 选项接管暂时停用（会破坏 AutoSlay 流程）
    }

    // ---------- 只在 QQ 起的对局里才接管点击 ----------
    static bool _agentMode;
    static bool _agentModeChecked;
    static readonly string AutoplayFlag2 = Paths.Data + @"\autoplay.flag";

    static bool AgentMode()
    {
        if (_agentModeChecked) return _agentMode;
        _agentModeChecked = true;
        try
        {
            bool byFlag = false, byActive = false;
            if (File.Exists(AutoplayFlag2))
            {
                DateTime t = File.GetLastWriteTime(AutoplayFlag2);
                byFlag = (DateTime.Now - t).TotalMinutes <= 5;
            }
            string activePath = Paths.Data + @"\autoplay.active";
            if (File.Exists(activePath))
            {
                DateTime t2 = File.GetLastWriteTime(activePath);
                byActive = (DateTime.Now - t2).TotalHours <= 6;
            }
            if (!byFlag && !byActive) { W("手动模式：不接管点击"); return _agentMode = false; }
            W("Agent 模式：接管点击（flag=" + byFlag + " active=" + byActive + "）");
            return _agentMode = true;
        }
        catch { return _agentMode = false; }
    }

    // ---------- 路线评分（一幕内口径） ----------
    static readonly string RouteRulesPath = Paths.Kb + @"\route-rules.tsv";
    static readonly Dictionary<string, int> _routeBase = new Dictionary<string, int>();

    static void LoadRouteRules()
    {
        if (_routeBase.Count > 0) return;
        _routeBase["Monster"] = 60; _routeBase["Elite"] = 50; _routeBase["Event"] = 55;
        _routeBase["RestSite"] = 65; _routeBase["Shop"] = 60; _routeBase["Treasure"] = 70; _routeBase["Unknown"] = 58;
        try
        {
            if (!File.Exists(RouteRulesPath)) return;
            string[] lines = File.ReadAllLines(RouteRulesPath, Encoding.UTF8);
            for (int i = 1; i < lines.Length; i++)
            {
                string[] f = lines[i].Split('\t');
                if (f.Length < 2) continue;
                int v;
                if (int.TryParse(f[1], out v)) _routeBase[f[0].Trim()] = v;
            }
            W("路线规则已载入: " + _routeBase.Count + " 类");
        }
        catch (Exception e) { W("路线规则载入失败: " + e.Message); }
    }

    static int I(object o, string n)
    {
        try { object v = Prop(o, n); return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; }
    }

    static List<object> L(object seq)
    {
        List<object> r = new List<object>();
        System.Collections.IEnumerable e = seq as System.Collections.IEnumerable;
        if (e != null) foreach (object o in e) r.Add(o);
        return r;
    }

    static string BaseType(string t)
    {
        if (string.IsNullOrEmpty(t)) return "Unknown";
        string[] keys = new string[] { "Monster", "Elite", "Event", "RestSite", "Shop", "Treasure" };
        for (int i = 0; i < keys.Length; i++)
            if (t.IndexOf(keys[i], StringComparison.OrdinalIgnoreCase) >= 0) return keys[i];
        return "Unknown";
    }

    // 一幕内已通过的普通怪数量
    static int MonstersThisAct(object screen)
    {
        int n = 0;
        try
        {
            Type rm = _sts2.GetType("MegaCrit.Sts2.Core.Runs.RunManager", false);
            object rmi = rm.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
            object st = Prop(rmi, "State") ?? Field(rmi, "<State>k__BackingField");
            List<object> visited = L(Prop(st, "VisitedMapCoords"));
            IDictionary dict = Field(screen, "_mapPointDictionary") as IDictionary;
            if (dict == null || visited.Count == 0) return 0;
            for (int i = 0; i < visited.Count; i++)
            {
                object coord = visited[i];
                foreach (DictionaryEntry de in dict)
                {
                    if (!de.Key.Equals(coord)) continue;
                    NMapPoint p = de.Value as NMapPoint;
                    object pt = p == null ? null : Prop(Prop(p, "Point"), "PointType");
                    string ty = S(pt ?? Prop(Prop(p, "Point"), "Type"));
                    if (BaseType(ty) == "Monster") n++;
                    break;
                }
            }
        }
        catch { }
        return n;
    }

    static int HpPercent()
    {
        try
        {
            Type rm = _sts2.GetType("MegaCrit.Sts2.Core.Runs.RunManager", false);
            object rmi = rm.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
            object st = Prop(rmi, "State") ?? Field(rmi, "<State>k__BackingField");
            IList ps = Prop(st, "Players") as IList;
            if (ps == null || ps.Count == 0) return 100;
            object cre = Prop(ps[0], "Creature");
            int hp = I(cre, "CurrentHp"), max = I(cre, "MaxHp");
            return max <= 0 ? 100 : (int)(hp * 100.0 / max);
        }
        catch { return 100; }
    }

    // 返回 score 与理由
    static void RouteScore(string type, int hpPct, int monstersSoFar, out int score, out string why)
    {
        LoadRouteRules();
        string bt = BaseType(type);
        int s;
        _routeBase.TryGetValue(bt, out s);
        StringBuilder sb = new StringBuilder();
        sb.Append(bt).Append(" ").Append(s);

        if (bt == "RestSite" && hpPct < 30) { s += 25; sb.Append(" +血少25"); }
        if (bt == "Elite" && hpPct < 50) { s -= 25; sb.Append(" -血少25"); }
        if (bt == "Elite" && hpPct > 70) { s += 20; sb.Append(" +血厚20"); }
        if (bt == "Monster")
        {
            if (monstersSoFar < 3) { s += 10; sb.Append(" +前三次10"); }
            else { s -= 10; sb.Append(" -已过多10"); }
        }
        score = Math.Max(1, Math.Min(120, s));
        why = sb.ToString();
    }

    static bool PrefixForceClick(object __instance, object[] __args)
    {
        if (AgentSeatEntry.RunEndedNow()) return true;
        if (!AgentMode()) return true;
        if (_inFallback) return true;
        if (!IsMapPoint(__instance))
        {
            string tn2 = __instance == null ? "" : __instance.GetType().Name;
            if (tn2 == "NEventOptionButton" || tn2 == "NRestSiteButton") return HandleOption(__instance);
            return true;
        }

        // 拦住这次点击：立刻放行（不阻塞），后台去问她
        NMapScreen screen = null;
        RunOnMain(new Action(() => { screen = MapScreen(); }));
        if (screen == null) return true;
        if (_asking) { W("已有请求在跑，忽略这次点击"); return false; }

        _asking = true;
        object node = __instance;
        NMapScreen sc = screen;
        Task.Run(new Action(() =>
        {
            try
            {
                List<NMapPoint> travel = new List<NMapPoint>();
                List<string> coords = new List<string>();
                List<object> models = new List<object>();
                RunOnMain(new Action(() =>
                {
                    IDictionary dict = Field(sc, "_mapPointDictionary") as IDictionary;
                    if (dict == null) return;
                    foreach (DictionaryEntry de in dict)
                    {
                        NMapPoint p = de.Value as NMapPoint;
                        if (p == null) continue;
                        object tv = Prop(p, "IsTravelable");
                        if (!(tv is bool) || !(bool)tv) continue;
                        travel.Add(p); coords.Add(S(de.Key)); models.Add(Prop(p, "Point"));
                    }
                }));
                if (travel.Count == 0) { W("没有可走点，放行原始点击"); FallbackClick(node); return; }

                int pick = 0;
                if (travel.Count > 1)
                {
                    string resp = Post(BuildNodesJson(sc, travel, coords, models));
                Match m = Regex.Match(resp ?? "", "\"index\"\\s*:\\s*(-?\\d+)");
                    int got = m.Success ? int.Parse(m.Groups[1].Value) : -1;
                    if (got >= 0 && got < travel.Count) pick = got;
                }
                W("地图点击 → 她要 " + coords[pick] + "（共 " + travel.Count + " 个可走点）");
                if (IsNodeDisabled(travel[pick])) { W("目标节点被禁用，放弃这次点击"); return; }
                NMapPoint chosen = travel[pick];
                // 点击后校验：地图没关就重试（最多 3 次）
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    RunOnMain(new Action(() => { ForceClickNode(chosen); }));
                    Thread.Sleep(1200);
                    bool stillOpen = false;
                    RunOnMain(new Action(() =>
                    {
                        try
                        {
                            Type mt = _sts2.GetType("MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen", false);
                            PropertyInfo ip = mt == null ? null : mt.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                            object map = ip == null ? null : ip.GetValue(null, null);
                            CanvasItem ci = map as CanvasItem;
                            stillOpen = map != null && ci != null && ci.IsVisibleInTree();
                        }
                        catch { stillOpen = false; }
                    }));
                    if (!stillOpen) break;
                    if (attempt < 2) W("地图没反应，重点一次（第 " + (attempt + 1) + " 次）");
                }
            }
            catch (Exception e) { W("地图拦截失败，放行原始点击: " + e.Message); FallbackClick(node); }
            finally { _asking = false; }
        }));
        return false;
    }

    // 节点是否被禁用（禁用的选项绝不能强点）
    static bool IsNodeDisabled(object node)
    {
        if (node == null) return true;
        string[] props = new string[] { "Disabled", "IsDisabled", "IsLocked", "Locked", "IsInteractable", "Interactable", "IsEnabled", "CanBeClicked" };
        string[] fields = new string[] { "_disabled", "_isDisabled", "_locked", "_isLocked", "_interactable" };
        for (int i = 0; i < props.Length; i++)
        {
            try
            {
                object v = Prop(node, props[i]);
                if (v is bool)
                {
                    bool b = (bool)v;
                    if (props[i] == "IsInteractable" || props[i] == "Interactable" || props[i] == "IsEnabled" || props[i] == "CanBeClicked") { if (!b) return true; }
                    else if (b) return true;
                }
            }
            catch { }
        }
        for (int i = 0; i < fields.Length; i++)
        {
            try
            {
                object v = Field(node, fields[i]);
                if (v is bool && (bool)v) return true;
            }
            catch { }
        }
        return false;
    }

    static void FallbackClick(object node)
    {
        RunOnMain(new Action(() => { ForceClickNode(node); }));
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

    static void Install(Assembly a)
    {
        try
        {
            Harmony h = new Harmony("clickseat");
            Type base_ = null;
            foreach (Type t in a.GetTypes())
            {
                if (t == null) continue;
                if (t.Name == "NClickableControl") { base_ = t; break; }
            }
            if (base_ == null) { W("找不到 NClickableControl"); return; }
            W("基类: " + base_.FullName);

            MethodInfo fc = null;
            foreach (MethodInfo m in base_.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (m.Name == "ForceClick") { fc = m; break; }
            if (fc == null) { W("基类上没有 ForceClick"); return; }

            MethodInfo pre = typeof(ClickSeatEntry).GetMethod("PrefixForceClick", BindingFlags.Static | BindingFlags.NonPublic);
            h.Patch(fc, new HarmonyMethod(pre), null, null);

            Type ui = a.GetType("MegaCrit.Sts2.Core.AutoSlay.Helpers.UiHelper", false);
            MethodInfo preUi = typeof(ClickSeatEntry).GetMethod("PrefixUiClick", BindingFlags.Static | BindingFlags.NonPublic);
            int un = 0;
            foreach (MethodInfo m in ui.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.Name != "Click") continue;
                if (m.IsGenericMethodDefinition) continue;
                try { h.Patch(m, new HarmonyMethod(preUi), null, null); un++; } catch { }
            }

            _installed = true;
            W("已挂 ForceClick + UiHelper.Click 拦截（Click 重载 " + un + " 个）");
        }
        catch (Exception e) { W("安装失败: " + e); }
    }

    public static void Initialize()
    {
        try { File.WriteAllText(Out, "", new UTF8Encoding(false)); } catch { }
        SceneTree t = Engine.GetMainLoop() as SceneTree;
        if (t != null) t.ProcessFrame += OnFrame;
    }

    static void OnFrame()
    {
        if (_mainThreadId == 0) _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        Action a;
        int n = 0;
        while (n < 8 && _mainQ.TryDequeue(out a)) { try { a(); } catch { } n++; }

        _frames++;
        Assembly sts2 = FindSts2();
        if (sts2 == null) return;
        if (!_installed) { Install(sts2); return; }
        if (_started || _frames < 60) return;
        try
        {
            Type gt = sts2.GetType("MegaCrit.Sts2.Core.Nodes.NGame");
            object game = gt.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
            if (game == null) return;
            object done = gt.GetProperty("GameStartupComplete", BindingFlags.Public | BindingFlags.Instance).GetValue(game, null);
            bool ready = (done is bool) ? (bool)done : (done is Task && ((Task)done).IsCompleted);
            if (!ready) return;
            if (gt.GetProperty("MainMenu", BindingFlags.Public | BindingFlags.Instance).GetValue(game, null) == null) return;
            _started = true;
            W("ClickSeat 就绪（AutoSlay 由 AgentSeat 启动）");
        }
        catch (Exception e) { W("启动失败: " + e.Message); }
    }
}

