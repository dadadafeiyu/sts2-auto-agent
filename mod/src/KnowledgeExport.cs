using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Modding;

// 知识库导出：把 ModelDb 里的卡牌/遗物/药水/能力/怪物导出成 JSON
// 触发条件：文件缺失，或 meta 里的指纹（版本 + 条目数）变了
// [ModInitializer] 由 SuiteEntry 统一调用
public static class KnowledgeExportEntry
{
    static readonly string LogPath = Paths.Logs + @"\knowledge.log";
    static readonly string Dir = Paths.Kb;
    static readonly string MetaPath = Paths.Kb + @"\meta.json";
    static readonly string GameDir = Paths.Game ?? "";

    static int frames;
    static int _upDebugCount;
    static bool done;
    static Assembly _sts2;

    static void W(string s) { try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\r\n", new UTF8Encoding(false)); } catch { } }

    static Assembly FindSts2()
    {
        if (_sts2 != null) return _sts2;
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        { try { if (x(a)) { _sts2 = a; return _sts2; } } catch { } }
        return null;
    }
    static bool x(Assembly a) { return a.GetName().Name == "sts2"; }

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

    static string J(string s)
    {
        if (s == null) return "";
        StringBuilder b = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') b.Append("\\\"");
            else if (c == '\\') b.Append("\\\\");
            else if (c == '\n') b.Append("\\n");
            else if (c == '\r') { }
            else if (c == '\t') b.Append(" ");
            else if (c < 0x20) b.Append(" ");
            else b.Append(c);
        }
        return b.ToString();
    }

    static string GetFieldVal(object o, string name)
    {
        try
        {
            if (o == null) return "?";
            FieldInfo fi = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null) return "?";
            object v = fi.GetValue(o);
            return v == null ? "null" : Convert.ToString(v);
        }
        catch { return "?"; }
    }

    // 导出全部动态变量 Name=Value;...（含 WeakPower/VulnerablePower 这类）
    // 导出卡牌固有词条（Sly/Retain/Exhaust 等）
    static string KeywordsOf(object model)
    {
        try
        {
            IEnumerable en = Prop(model, "CanonicalKeywords") as IEnumerable;
            if (en == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (object k in en)
            {
                try
                {
                    string nm;
                    if (k is Enum) nm = k.ToString();          // CardKeyword 枚举
                    else
                    {
                        nm = Convert.ToString(Prop(k, "Name"));
                        if (string.IsNullOrEmpty(nm)) nm = Convert.ToString(Prop(k, "Title"));
                    }
                    if (string.IsNullOrEmpty(nm) || nm == "None") continue;
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(nm.Replace("\"", ""));
                }
                catch { }
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    static string AllVars(object model)
    {
        try
        {
            object dv = Prop(model, "DynamicVars");
            if (dv == null) return "";
            IEnumerable en = Prop(dv, "Values") as IEnumerable;
            if (en == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (object v in en)
            {
                try
                {
                    string nm = Convert.ToString(Prop(v, "Name"));
                    object iv = Prop(v, "IntValue");
                    if (string.IsNullOrEmpty(nm) || iv == null) continue;
                    if (sb.Length > 0) sb.Append(';');
                    sb.Append(nm).Append('=').Append(Convert.ToInt32(iv));
                }
                catch { }
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    static string VarText(object model)
    {
        try
        {
            object dv = Prop(model, "DynamicVars");
            if (dv == null) return "";
            object dmg = Prop(dv, "Damage");
            object blk = Prop(dv, "Block");
            StringBuilder sb = new StringBuilder();
            if (dmg != null) sb.Append("dmg=").Append(Convert.ToInt32(Prop(dmg, "IntValue")));
            if (blk != null) { if (sb.Length > 0) sb.Append(';'); sb.Append("block=").Append(Convert.ToInt32(Prop(blk, "IntValue"))); }
            return sb.ToString();
        }
        catch { return ""; }
    }

    static string GameVersion()
    {
        try
        {
            string p = Path.Combine(GameDir, "release_info.json");
            if (!File.Exists(p)) return "?";
            string s = File.ReadAllText(p);
            int i = s.IndexOf("\"version\"");
            if (i < 0) return "?";
            int c = s.IndexOf(':', i);
            int q1 = s.IndexOf('"', c + 1);
            int q2 = s.IndexOf('"', q1 + 1);
            if (q1 < 0 || q2 < 0) return "?";
            return s.Substring(q1 + 1, q2 - q1 - 1);
        }
        catch { return "?"; }
    }

    // 克隆 + 升级，取升级后的数值
    // 解析 {IfUpgraded:show:升级版|普通版}
    static string ResolveUpgraded(string desc, bool upgraded)
    {
        if (string.IsNullOrEmpty(desc)) return desc;
        return System.Text.RegularExpressions.Regex.Replace(desc,
            "\\{IfUpgraded:show:([^|{}]*)\\|([^|{}]*)\\}",
            delegate (System.Text.RegularExpressions.Match m) { return upgraded ? m.Groups[1].Value : m.Groups[2].Value; });
    }

    // 从 CardEnergyCost.Canonical 读真实费用（升级会改这里，CanonicalEnergyCost 不会）
    static int RealCost(object card)
    {
        try
        {
            object ec = Prop(card, "EnergyCost");
            if (ec != null)
            {
                object v = Prop(ec, "Canonical");
                if (v != null) return Convert.ToInt32(v);
            }
            return IntOf(card, "CanonicalEnergyCost");
        }
        catch { return IntOf(card, "CanonicalEnergyCost"); }
    }

    static int IntOf(object o, string n)
    {
        try { object v = Prop(o, n); return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; }
    }

    // 从描述里猜段数（"两次" / "3次" / "2 次"）
    static int HitsFromText(string desc)
    {
        if (string.IsNullOrEmpty(desc)) return 1;
        if (desc.IndexOf("两次") >= 0) return 2;
        if (desc.IndexOf("三次") >= 0) return 3;
        if (desc.IndexOf("四次") >= 0) return 4;
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(desc, "([0-9]+)\\s*次");
        if (m.Success)
        {
            int v;
            if (int.TryParse(m.Groups[1].Value, out v) && v > 0 && v <= 20) return v;
        }
        return 1;
    }

    // 费效：
    //   只有攻击 -> 总攻击 / 费用
    //   只有防御 -> 防御×1.2 / 费用
    //   攻防都有 -> (攻击 + 防御×1.2) / (费用×2)
    static string EffJson(object model)
    {
        try
        {
            object dv = Prop(model, "DynamicVars");
            int dmg = 0, blk = 0;
            if (dv != null)
            {
                object d = Prop(dv, "Damage");
                object b = Prop(dv, "Block");
                if (d != null) dmg = IntOf(d, "IntValue");
                if (b != null) blk = IntOf(b, "IntValue");
            }
            if (dmg == 0 && blk == 0) return "";

            string desc = ResolveUpgraded(Loc(Prop(model, "Description")), true);
            int hits = Math.Max(Math.Max(1, IntOf(model, "BaseReplayCount")), HitsFromText(desc));
            int total = dmg * hits;
            int cost = RealCost(model);
            int starCost = StarOf(model);
            object xo = Prop(model, "HasEnergyCostX");
            bool costX = xo is bool && (bool)xo;

            bool aoe = desc.IndexOf("所有敌人") >= 0 || desc.IndexOf("每名敌人") >= 0 || desc.IndexOf("每个敌人") >= 0;
            bool hitsVar = desc.IndexOf("{Repeat") >= 0;

            StringBuilder sb = new StringBuilder();
            if (starCost > 0) sb.Append(",\"star\":").Append(starCost);
            sb.Append(",\"eff\":{\"cost\":").Append(cost).Append(",\"dmg\":").Append(dmg)
              .Append(",\"hits\":").Append(hits).Append(",\"blk\":").Append(blk);
            if (aoe) sb.Append(",\"aoe\":true");
            if (hitsVar) sb.Append(",\"hitsVar\":\"Repeat\"");
            if (costX)
            {
                sb.Append(",\"costX\":true");
                sb.Append(",\"effPerEnergy\":").Append(Math.Round((double)total + blk * 1.2, 2).ToString("0.##"));
            }
            else if (cost <= 0) sb.Append(",\"free\":true");
            else
            {
                double eff;
                if (dmg > 0 && blk > 0) eff = (total + blk * 1.2) / (cost * 2);
                else if (dmg > 0) eff = (double)total / cost;
                else eff = blk * 1.2 / cost;
                sb.Append(",\"eff\":").Append(Math.Round(eff, 2).ToString("0.##"));
            }
            sb.Append('}');
            return sb.ToString();
        }
        catch { return ""; }
    }

    static int StarOf(object card)
    {
        try
        {
            string[] names = new string[] { "CanonicalStarCost", "CurrentStarCost", "BaseStarCost" };
            for (int i = 0; i < names.Length; i++)
            {
                object v = Prop(card, names[i]);
                if (v == null) continue;
                int n;
                if (int.TryParse(Convert.ToString(v), out n)) return n;
            }
        }
        catch { }
        return 0;
    }

    static string UpgradedVars(object card, out string descUp, out string effUp, out int costUp, out object upgraded)
    {
        descUp = "";
        effUp = "";
        costUp = -1;
        upgraded = null;
        try
        {
            MethodInfo clone = card.GetType().GetMethod("MutableClone", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (clone == null) { LogUpDebug(Loc(Prop(card, "Title")), "无 MutableClone"); return ""; }
            object dup = clone.Invoke(card, null);
            upgraded = dup;
            // 克隆出来的 CardEnergyCost 里 _card 仍指向原始(不可变)卡牌，
            // 会导致升级时 AssertMutable 失败、费用改不动 → 手动改指向克隆自身
            try
            {
                object ec = Prop(dup, "EnergyCost");
                FieldInfo ecCard = ec == null ? null : ec.GetType().GetField("_card", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ecCard != null) ecCard.SetValue(ec, dup);
            }
            catch { }
            Type cmdType = _sts2.GetType("MegaCrit.Sts2.Core.Commands.CardCmd", false);
            MethodInfo up = null;
            foreach (MethodInfo m in cmdType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (m.Name != "Upgrade") continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length >= 1 && ps[0].ParameterType.IsInstanceOfType(dup)) { up = m; break; }
            }
            if (up == null) { LogUpDebug(Loc(Prop(card, "Title")), "无 CardCmd.Upgrade"); return ""; }
            object[] args = new object[up.GetParameters().Length];
            args[0] = dup;
            for (int i = 1; i < args.Length; i++)
            {
                Type pt = up.GetParameters()[i].ParameterType;
                args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
            }
            string preInfo = "";
            try
            {
                if (_upDebugCount < 8)
                {
                    object ecp = Prop(dup, "EnergyCost");
                    preInfo = " [前] lvl=" + IntOf(dup, "CurrentUpgradeLevel")
                        + " 可升级=" + Convert.ToString(Prop(dup, "IsUpgradable"))
                        + " 费用base=" + GetFieldVal(ecp, "_base")
                        + " canonical=" + Prop(ecp, "Canonical")
                        + " 刚升级=" + Convert.ToString(Prop(ecp, "WasJustUpgraded"));
                }
            }
            catch { }
            object r = up.Invoke(null, args);
            Task t = r as Task;
            if (t != null && !t.Wait(2000)) { LogUpDebug(Loc(Prop(card, "Title")), "升级任务超时"); return ""; }
            descUp = Loc(Prop(dup, "Description"));
            effUp = EffJson(dup);
            try { costUp = RealCost(dup); } catch { costUp = -1; }
            if (_upDebugCount < 20)
            {
                _upDebugCount++;
                string postInfo = "";
                try
                {
                    object ecq = Prop(dup, "EnergyCost");
                    postInfo = " [后] lvl=" + IntOf(dup, "CurrentUpgradeLevel")
                        + " 费用base=" + GetFieldVal(ecq, "_base")
                        + " canonical=" + Prop(ecq, "Canonical")
                        + " 刚升级=" + Convert.ToString(Prop(ecq, "WasJustUpgraded"));
                }
                catch { }
                LogUpDebug(Loc(Prop(card, "Title")), "原费=" + RealCost(card) + " 升后=" + costUp + " 描述变化=" + (descUp != Loc(Prop(card, "Description"))) + preInfo + postInfo);
            }
            return VarText(dup);
        }
        catch (Exception ex) { LogUpDebug(Loc(Prop(card, "Title")), "异常:" + ex.Message); return ""; }
    }

    static void LogUpDebug(string title, string why)
    {
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " [升级失败] " + title + " → " + why + "\r\n", new UTF8Encoding(false)); } catch { }
    }

    // 把描述里出现的所有 {Name:diff()} 都解析成 Name=value
    static string VarTableFor(object model, string text)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return "";
            object dv = Prop(model, "DynamicVars");
            if (dv == null) return "";
            StringBuilder sb = new StringBuilder();
            List<string> seen = new List<string>();
            System.Text.RegularExpressions.MatchCollection ms =
                System.Text.RegularExpressions.Regex.Matches(text, "\\{([A-Za-z][A-Za-z0-9_]*):diff\\(\\)\\}");
            for (int i = 0; i < ms.Count; i++)
            {
                string name = ms[i].Groups[1].Value;
                if (seen.Contains(name)) continue;
                seen.Add(name);
                object v = Prop(dv, name);
                if (v == null) continue;
                double val = 0;
                try { val = Convert.ToDouble(Prop(v, "IntValue")); } catch { }
                if (sb.Length > 0) sb.Append(';');
                sb.Append(name).Append('=').Append(((int)val).ToString());
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    static void ExportList(string fileName, IEnumerable models, string kind)
    {
        if (models == null) { W(kind + ": 空"); return; }
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"kind\":\"").Append(kind).Append("\",\"items\":[");
        int n = 0;
        foreach (object m in models)
        {
            if (m == null) continue;
            try
            {
                if (n > 0) sb.Append(',');
                string id = Convert.ToString(Prop(m, "Id"));
                string title = Loc(Prop(m, "Title"));
                string desc = Loc(Prop(m, "Description"));
                if (desc.Length == 0) desc = Loc(Prop(m, "SmartDescription"));
                desc = ResolveUpgraded(desc, false);
                string type = Convert.ToString(Prop(m, "Type"));
                string cost = Convert.ToString(Prop(m, "CanonicalEnergyCost"));
                string rarity = Convert.ToString(Prop(m, "Rarity"));
                string vars = VarText(m);
                sb.Append("{\"id\":\"").Append(J(id)).Append('"')
                  .Append(",\"title\":\"").Append(J(title)).Append('"')
                  .Append(",\"desc\":\"").Append(J(desc)).Append('"');
                if (!string.IsNullOrEmpty(type) && type != "") sb.Append(",\"type\":\"").Append(J(type)).Append('"');
                if (!string.IsNullOrEmpty(cost) && cost != "0") sb.Append(",\"cost\":").Append(J(cost));
                if (!string.IsNullOrEmpty(rarity)) sb.Append(",\"rarity\":\"").Append(J(rarity)).Append('"');
                if (vars.Length > 0) sb.Append(",\"vars\":\"").Append(J(vars)).Append('"');
                if (kind == "cards")
                {
                    sb.Append(EffJson(m));
                    string vt0 = VarTableFor(m, desc);
                    if (!string.IsNullOrEmpty(vt0)) sb.Append(",\"varsAll\":\"").Append(J(vt0)).Append('"');
                    object pool0 = Prop(m, "Pool");
                    if (pool0 != null)
                    {
                        string pt0 = Loc(Prop(pool0, "Title"));
                        if (string.IsNullOrEmpty(pt0)) pt0 = Convert.ToString(Prop(pool0, "Id"));
                        sb.Append(",\"pool\":\"").Append(J(pt0)).Append('"');
                    }
                    try
                    {
                        string kws = KeywordsOf(m);
                        if (!string.IsNullOrEmpty(kws)) sb.Append(",\"keywords\":\"").Append(J(kws)).Append('"');
                    }
                    catch { }
                    string descUp, effUp; int costUp; object upCard;
                    string uv = UpgradedVars(m, out descUp, out effUp, out costUp, out upCard);
                    try
                    {
                        string dvA = AllVars(m);
                        string dvB = AllVars(upCard);
                        if (!string.IsNullOrEmpty(dvA)) sb.Append(",\"dv\":\"").Append(J(dvA)).Append('"');
                        if (!string.IsNullOrEmpty(dvB) && dvB != dvA) sb.Append(",\"dvUp\":\"").Append(J(dvB)).Append('"');
                    }
                    catch { }
                    if (costUp >= 0 && costUp != RealCost(m)) sb.Append(",\"costUp\":").Append(costUp);
                    if (uv.Length > 0 && uv != vars) sb.Append(",\"varsUp\":\"").Append(J(uv)).Append('"');
                    string up = ResolveUpgraded(descUp, true);
                    if (!string.IsNullOrEmpty(up) && up != desc) sb.Append(",\"descUp\":\"").Append(J(up)).Append('"');
                    if (!string.IsNullOrEmpty(effUp)) sb.Append(effUp.Replace("\"eff\"", "\"effUp\""));
                    string vt2 = VarTableFor(m, descUp);
                    if (!string.IsNullOrEmpty(vt2) && vt2 != vt0) sb.Append(",\"varsAllUp\":\"").Append(J(vt2)).Append('"');
                }
                sb.Append('}');
                n++;
            }
            catch (Exception e) { if (n == 0) W(kind + " 条目异常: " + e.Message); }
        }
        sb.Append("]}");
        File.WriteAllText(Path.Combine(Dir, fileName), sb.ToString(), new UTF8Encoding(false));
        W(kind + ": " + n + " 条 -> " + fileName);
    }

    // 事件导出：id / 标题 / 描述 / 选项
    static void ExportEvents(Assembly a)
    {
        try
        {
            Type db = a.GetType("MegaCrit.Sts2.Core.Models.ModelDb", false);
            PropertyInfo ap = db.GetProperty("AllEvents", BindingFlags.Public | BindingFlags.Static);
            IEnumerable evs = ap == null ? null : ap.GetValue(null, null) as IEnumerable;
            if (evs == null) { W("events: 无数据"); return; }

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"kind\":\"events\",\"items\":[");
            int n = 0;
            foreach (object ev in evs)
            {
                if (ev == null) continue;
                try
                {
                    if (n > 0) sb.Append(',');
                    string id = Convert.ToString(Prop(ev, "Id"));
                    string title = Loc(Prop(ev, "Title"));
                    string desc = Loc(Prop(ev, "Description"));
                    if (string.IsNullOrEmpty(desc)) desc = Loc(Prop(ev, "InitialDescription"));
                    sb.Append("{\"id\":\"").Append(J(id)).Append('"')
                      .Append(",\"title\":\"").Append(J(title)).Append('"')
                      .Append(",\"desc\":\"").Append(J(desc)).Append('"')
                      .Append(",\"options\":[");

                    object opts = Prop(ev, "AllPossibleOptions") ?? Prop(ev, "CurrentOptions");
                    IEnumerable seq = opts as IEnumerable;
                    int k = 0;
                    if (seq != null)
                    {
                        foreach (object o in seq)
                        {
                            if (o == null) continue;
                            if (k > 0) sb.Append(',');
                            string ot = Loc(Prop(o, "Title"));
                            string od = Loc(Prop(o, "Description"));
                            sb.Append("{\"title\":\"").Append(J(ot)).Append('"')
                              .Append(",\"desc\":\"").Append(J(od)).Append("\"}");
                            k++;
                            if (k >= 8) break;
                        }
                    }
                    sb.Append("]}");
                    n++;
                }
                catch { }
            }
            sb.Append("]}");
            File.WriteAllText(Path.Combine(Dir, "events.json"), sb.ToString(), new UTF8Encoding(false));
            W("events: " + n + " 条 -> events.json");
        }
        catch (Exception e) { W("事件导出失败: " + e.Message); }
    }

    public static void Initialize()
    {
        try { File.WriteAllText(LogPath, "", new UTF8Encoding(false)); } catch { }
        SceneTree t = Engine.GetMainLoop() as SceneTree;
        if (t != null) t.ProcessFrame += OnFrame;
    }

    static void OnFrame()
    {
        frames++;
        if (done || frames < 120) return;
        Assembly a = FindSts2();
        if (a == null) return;
        done = true;
        try
        {
            Directory.CreateDirectory(Dir);
            Type db = a.GetType("MegaCrit.Sts2.Core.Models.ModelDb", false);
            if (db == null) { W("找不到 ModelDb"); return; }

            IEnumerable cards = Prop(null, "x") as IEnumerable; // 占位，下面用静态属性
            cards = db.GetProperty("AllCards", BindingFlags.Public | BindingFlags.Static).GetValue(null, null) as IEnumerable;
            IEnumerable relics = db.GetProperty("AllRelics", BindingFlags.Public | BindingFlags.Static).GetValue(null, null) as IEnumerable;
            IEnumerable potions = db.GetProperty("AllPotions", BindingFlags.Public | BindingFlags.Static).GetValue(null, null) as IEnumerable;
            IEnumerable powers = db.GetProperty("AllPowers", BindingFlags.Public | BindingFlags.Static).GetValue(null, null) as IEnumerable;

            string ver = GameVersion();
            int nc = Count(cards), nr = Count(relics), np = Count(potions), nw = Count(powers);
            string fp = ver + "|" + nc + "|" + nr + "|" + np + "|" + nw;

            bool need = true;
            try
            {
                if (File.Exists(MetaPath))
                {
                    string old = File.ReadAllText(MetaPath);
                    if (old.IndexOf("\"fingerprint\":\"" + fp + "\"", StringComparison.Ordinal) >= 0) need = false;
                }
            }
            catch { }

            if (!need)
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " 指纹未变，跳过导出 (" + fp + ")\r\n");
                return;
            }

            ExportList("cards.json", cards, "cards");
            ExportList("relics.json", relics, "relics");
            ExportList("potions.json", potions, "potions");
            ExportList("powers.json", powers, "powers");

            ExportEvents(a);

            StringBuilder meta = new StringBuilder();
            meta.Append("{\"fingerprint\":\"").Append(fp).Append("\",\"gameVersion\":\"").Append(ver).Append('"')
                .Append(",\"cards\":").Append(nc).Append(",\"relics\":").Append(nr)
                .Append(",\"potions\":").Append(np).Append(",\"powers\":").Append(nw)
                .Append(",\"exportedAt\":\"").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\"}");
            File.WriteAllText(MetaPath, meta.ToString(), new UTF8Encoding(false));
            W("导出完成 " + fp);
        }
        catch (Exception e) { W("导出失败: " + e); }
    }

    static int Count(IEnumerable seq)
    {
        if (seq == null) return -1;
        int n = 0;
        foreach (object o in seq) n++;
        return n;
    }
}

