using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// 知识库：卡牌/遗物评分、升级分、词条、动态调整（从 AgentSeat.cs 抽出来的独立模块）
public static partial class AgentSeatEntry
{
    // ================= 知识库（模组侧） =================
    class CardInfo
    {
        public string Id, Title, Pool, Rarity, Desc, DescUp;
        public int Cost, Dmg, Hits, Blk;
        public double Eff, EffUp;
        public bool Aoe, NeedsEnergy;
        public string Keywords = "";
    }

    class DeckInfo
    {
        public int Size, Expensive, Energy, Exhaust, Draw, Block, Attacks;
        public List<string> Titles = new List<string>();
    }

    static readonly Dictionary<string, CardInfo> _kbById = new Dictionary<string, CardInfo>();
    static readonly Dictionary<string, List<CardInfo>> _kbByTitle = new Dictionary<string, List<CardInfo>>();
    static readonly Dictionary<string, int> _kbScore = new Dictionary<string, int>();
    static readonly Dictionary<string, int> _kbUp = new Dictionary<string, int>();
    static readonly Dictionary<string, string> _kbKw = new Dictionary<string, string>();   // 词条（Sly/Retain/...）
    static readonly Dictionary<string, string> _kbTier = new Dictionary<string, string>();
    static readonly Dictionary<string, int> _relicScore = new Dictionary<string, int>();
    static readonly Dictionary<string, string> _relicTier = new Dictionary<string, string>();
    static bool _kbLoaded;
    static bool _indexWarned;   // 索引过期提示只打一次
    static bool _llmHandlerLogged;
    static string _pendingPurpose = "";   // 由 CardSelectCmd 的入口方法名推断（选牌钩子拿不到 prefs）
    static DateTime _kbStamp = DateTime.MinValue;
    static readonly string KbDir = Paths.Kb;

    static int IntOr(string s, int dflt)
    {
        int v;
        return int.TryParse(s, out v) ? v : dflt;
    }

    static double DblOr(string s, double dflt)
    {
        double v;
        return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : dflt;
    }

    static void EnsureKnowledge()
    {
        try
        {
            string cp = Path.Combine(KbDir, "cards.index.tsv");
            string sp = Path.Combine(KbDir, "scores.tsv");
            if (!File.Exists(cp)) { W("知识库缺失: " + cp); return; }
            DateTime stamp = File.GetLastWriteTime(cp);

            // 检查索引是否落后于 cards.json（游戏更新后需要重跑 build_index.py）
            try
            {
                string jp = Path.Combine(KbDir, "cards.json");
                if (File.Exists(jp))
                {
                    DateTime jt = File.GetLastWriteTime(jp);
                    if (jt > stamp.AddSeconds(1))
                    {
                        if (!_indexWarned)
                        {
                            _indexWarned = true;
                            W("⚠️ 索引过期：cards.json 比 cards.index.tsv 新（" + jt.ToString("MM-dd HH:mm") +
                              " > " + stamp.ToString("MM-dd HH:mm") + "），请运行 build_index.py 重新生成");
                        }
                    }
                }
            }
            catch { }
            if (File.Exists(sp))
            {
                DateTime s2 = File.GetLastWriteTime(sp);
                if (s2 > stamp) stamp = s2;
            }
            if (_kbLoaded && stamp == _kbStamp) return;

            _kbById.Clear(); _kbByTitle.Clear(); _kbScore.Clear(); _kbTier.Clear(); _kbUp.Clear(); _kbKw.Clear();

            string[] lines = File.ReadAllLines(cp, Encoding.UTF8);
            for (int i = 1; i < lines.Length; i++)
            {
                string[] f = lines[i].Split('\t');
                if (f.Length < 14) continue;
                CardInfo c = new CardInfo();
                c.Id = f[0]; c.Title = f[1]; c.Pool = f[2];
                c.Cost = IntOr(f[3], 0); c.Rarity = f[4];
                c.Dmg = IntOr(f[5], 0); c.Hits = IntOr(f[6], 1); c.Blk = IntOr(f[7], 0);
                c.Eff = DblOr(f[8], 0); c.EffUp = DblOr(f[9], 0);
                c.Aoe = f[10] == "1"; c.NeedsEnergy = f[11] == "1";
                c.Desc = f[12]; c.DescUp = f[13];
                _kbById[c.Id] = c;
                if (!string.IsNullOrEmpty(c.Title))
                {
                    List<CardInfo> l;
                    if (!_kbByTitle.TryGetValue(c.Title, out l)) { l = new List<CardInfo>(); _kbByTitle[c.Title] = l; }
                    l.Add(c);
                }
            }

            if (File.Exists(sp))
            {
                string[] sl = File.ReadAllLines(sp, Encoding.UTF8);
                for (int i = 1; i < sl.Length; i++)
                {
                    string[] f = sl[i].Split('\t');
                    if (f.Length < 4) continue;
                    _kbScore[f[0] + "|" + f[1]] = IntOr(f[2], 0);
                    _kbTier[f[0] + "|" + f[1]] = f[3];
                    if (f.Length >= 5 && !string.IsNullOrEmpty(f[4].Trim())) _kbUp[f[0] + "|" + f[1]] = IntOr(f[4], -1);
                    if (f.Length >= 7 && !string.IsNullOrEmpty(f[6].Trim())) _kbKw[f[0] + "|" + f[1]] = f[6].Trim();
                }
            }

            string rp = Path.Combine(KbDir, "relic_scores.tsv");
            if (File.Exists(rp))
            {
                string[] rl = File.ReadAllLines(rp, Encoding.UTF8);
                for (int i = 1; i < rl.Length; i++)
                {
                    string[] f = rl[i].Split('\t');
                    if (f.Length < 3) continue;
                    _relicScore[f[0]] = IntOr(f[1], 0);
                    _relicTier[f[0]] = f[2];
                }
            }

            _kbLoaded = true;
            _kbStamp = stamp;
            W("知识库已载入: 卡 " + _kbById.Count + " / 分 " + _kbScore.Count);
        }
        catch (Exception e) { W("知识库载入失败: " + e.Message); }
    }

    static CardInfo FindCardInfo(string title, string ch)
    {
        EnsureKnowledge();
        if (string.IsNullOrEmpty(title)) return null;
        List<CardInfo> list;
        if (!_kbByTitle.TryGetValue(title, out list) || list.Count == 0) return null;
        if (list.Count == 1) return list[0];
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].Pool, ch, StringComparison.OrdinalIgnoreCase)) return list[i];
        return list[0];
    }

    // 局内调整（复刻插件里的 6 条规则）
    // 当前幕（1 起）
    static int CurrentAct()
    {
        try
        {
            object rs = RunStateNow();
            if (rs == null) return 1;
            object v = Prop(rs, "CurrentActIndex");
            if (v != null) return Convert.ToInt32(v) + 1;
            return 1;
        }
        catch { return 1; }
    }

    // 这张牌是不是毒源（按名字判断，卡组里只有标题）
    static readonly string[] PoisonTitles = new string[] {
        "致命毒药","弹跳药瓶","剧毒匕首","蛇咬","涂毒","幻影","毒雾","幻雾","灾厄降临","腐蚀波","气泡","混合物"
    };
    static bool PoisonTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        for (int i = 0; i < PoisonTitles.Length; i++)
            if (title.IndexOf(PoisonTitles[i], StringComparison.Ordinal) >= 0) return true;
        return false;
    }

    static int AdjustScore(CardInfo c, DeckInfo d, int baseScore, out string notes)
    {
        notes = "";
        if (c == null || d == null) return baseScore;
        int s = baseScore;
        List<string> ns = new List<string>();

        if (c.NeedsEnergy)
        {
            if (d.Energy == 0) { s -= 12; ns.Add("无加费-12"); }
            else if (d.Energy >= 2) { s += 4; ns.Add("有加费+4"); }
        }
        int dup = 0;
        for (int i = 0; i < d.Titles.Count; i++) if (d.Titles[i] == c.Title) dup++;
        if (dup >= 3) { s -= 26; ns.Add("已有" + dup + "张-26"); }
        else if (dup >= 2) { s -= 14; ns.Add("已有" + dup + "张-14"); }
        else if (dup == 1) { s -= 6; ns.Add("已有一张-6"); }

        if (d.Expensive >= 8 && c.Cost >= 2) { s -= 4; ns.Add("曲线偏重-4"); }

        // 毒牌重复惩罚更狠：毒是叠加型，第 2、3 张的边际收益明显下降
        bool isPoisonCard = (c.Desc ?? "").IndexOf("毒") >= 0;
        if (isPoisonCard)
        {
            if (dup >= 3) { s -= 34; ns.Add("毒牌重复" + dup + "张-34"); }
            else if (dup >= 2) { s -= 24; ns.Add("毒牌重复" + dup + "张-24"); }
            else if (dup == 1) { s -= 12; ns.Add("毒牌重复-12"); }
        }

        string why = c.Desc ?? "";
        if (why.IndexOf("消耗") >= 0)
        {
            if (d.Exhaust >= 4) { s += 5; ns.Add("消耗体系+5"); }
            else if (d.Exhaust == 0) { s -= 4; ns.Add("无消耗支撑-4"); }
        }


        // 成长牌：灰烬打击伤害来自"消耗牌堆"，需要卡组真能消耗
        if (c.Title == "灰烬打击")
        {
            if (d.Exhaust >= 3) { s += 30; ns.Add("消耗多+30"); }
            else if (d.Exhaust >= 1) { s += 18; ns.Add("有消耗+18"); }
            else { s -= 10; ns.Add("零消耗-10"); }
        }

        // 无惧疼痛：只有卡组里有消耗核心牌时才抬分
        if (c.Title == "无惧疼痛")
        {
            string[] partners = new string[] { "添柴", "腐化", "恶魔之焰", "重振精神" };
            string hitP = null;
            for (int i = 0; i < d.Titles.Count && hitP == null; i++)
                for (int j = 0; j < partners.Length; j++)
                    if (d.Titles[i] == partners[j]) { hitP = partners[j]; break; }
            if (hitP != null) { s += 22; ns.Add("有" + hitP + "+22"); }
            else if (d.Exhaust >= 1) { ns.Add("有消耗牌"); }
            else { s -= 15; ns.Add("卡组零消耗-15"); }
        }

        // ===== 全局动态评分 =====
        // ① 攻防失衡：攻多则防御牌涨价，防多则攻击牌涨价
        if (d.Attacks > d.Block + 3 && c.Blk > 0)
        {
            s += 8; ns.Add("缺防+8");
        }
        else if (d.Block > d.Attacks + 3 && c.Dmg > 0)
        {
            s += 8; ns.Add("缺攻+8");
        }

        // ② 卡组没有加费牌时，加费牌涨价
        if (d.Energy == 0 && c.Desc != null && c.Desc.IndexOf("能量") >= 0 && c.Desc.IndexOf("获得") >= 0)
        {
            s += 10; ns.Add("无加费+10");
        }

        // ③ 卡组越厚，抽牌牌越值钱（分档，不叠加）
        if (c.Desc != null && c.Desc.IndexOf("抽") >= 0)
        {
            if (d.Size >= 30) { s += 12; ns.Add("卡组超厚抽牌+12"); }
            else if (d.Size >= 25) { s += 8; ns.Add("卡组厚抽牌+8"); }
            else if (d.Size >= 20) { s += 4; ns.Add("卡组偏厚抽牌+4"); }
        }

        // 普通（Common）高分局：第一幕吃香，第二幕起暴跌
        if (!string.IsNullOrEmpty(c.Rarity) && c.Rarity.IndexOf("Common", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            int act = CurrentAct();
            if (act >= 2 && s >= 55)
            {
                s -= 30;
                ns.Add("二幕普通牌-30");
            }
            else if (act == 1 && s >= 55)
            {
                s += 5;
                ns.Add("一幕普通牌+5");
            }
        }

        // 腐蚀波：每当你抽到一张牌就叠毒 → 抽牌越多越强
        if (c.Title == "腐蚀波" || (c.Desc ?? "").IndexOf("每当你抽到") >= 0)
        {
            int bonus = Math.Min(d.Draw * 3, 18);
            if (bonus > 0) { s += bonus; ns.Add("抽牌" + d.Draw + "张+" + bonus); }
            else { s -= 8; ns.Add("无抽牌-8"); }
        }

        // 触媒联动：卡组里有触媒 → 毒牌加分、触媒本身扣分
        try
        {
            // 卡组里的毒源张数（不含触媒本身）
            int poisonSrc = 0;
            for (int q = 0; q < d.Titles.Count; q++)
                if (d.Titles[q] != "触媒" && PoisonTitle(d.Titles[q])) poisonSrc++;
            if (c.Title == "触媒")
            {
                if (poisonSrc >= 2) { s += 10; ns.Add("毒源" + poisonSrc + "张+10"); }
                else { s = Math.Min(s, 80); ns.Add("毒源不足" + poisonSrc + "张→80"); }
            }
            bool hasCatalyst = false;
            for (int i = 0; i < d.Titles.Count; i++) if (d.Titles[i] == "触媒") { hasCatalyst = true; break; }
            if (hasCatalyst)
            {
                if (c.Title == "触媒") { s -= 30; ns.Add("已有触媒-30"); }
                else if ((c.Desc ?? "").Contains("中毒") || (c.Desc ?? "").Contains("毒"))
                {
                    s += 25; ns.Add("有触媒+25");
                }
            }
        }
        catch { }

        notes = string.Join(",", ns.ToArray());
        return Math.Max(1, Math.Min(100, s));
    }
}
