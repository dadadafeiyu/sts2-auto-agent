using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

// 药水：读取 / 使用 / 丢弃（参考 dsh-sts2-main 的 Potions.cs）
public static partial class AgentSeatEntry
{
    // 读药水槽：Slot { Index, Potion, Title, Usable, Reason }
    internal class PotionSlotInfo
    {
        public int Index;
        public object Potion;
        public string Title = "";
        public string Desc = "";
        public bool Usable = true;
        public string Reason = "";
    }

    static List<PotionSlotInfo> PotionSlots(object player)
    {
        List<PotionSlotInfo> list = new List<PotionSlotInfo>();
        try
        {
            IEnumerable slots = Prop(player, "PotionSlots") as IEnumerable;
            if (slots == null) return list;
            int idx = 0;
            foreach (object s in slots)
            {
                if (s == null) { idx++; continue; }
                // 注意：Player.PotionSlots 直接就是 IReadOnlyList<PotionModel>，每个元素本身是药水
                object potion = s;
                PotionSlotInfo info = new PotionSlotInfo();
                info.Index = idx;
                info.Potion = potion;
                if (potion != null)
                {
                    info.Title = Loc(Prop(potion, "Title"));
                    if (string.IsNullOrEmpty(info.Title)) info.Title = S(Prop(potion, "Id"));
                    string d = Loc(Prop(potion, "DynamicDescription"));
                    if (string.IsNullOrEmpty(d)) d = Loc(Prop(potion, "Description"));
                    info.Desc = d != null && d.Length > 50 ? d.Substring(0, 50) : d;
                }
                list.Add(info);
                idx++;
            }
        }
        catch (Exception e) { W("读药水槽失败: " + e.Message); }
        return list;
    }

    // 能不能用：读 UsePotionAction 会校验的那个字段（PotionModel.CanUse / Usable）
    static bool PotionUsable(object potion, out string why)
    {
        why = "";
        try
        {
            object v = Prop(potion, "Usable");
            if (v is bool) { if (!(bool)v) { why = "现在不能用"; return false; } }
            object c = Prop(potion, "CanUse");
            if (c is bool) { if (!(bool)c) { why = "现在不能用"; return false; } }
        }
        catch { }
        return true;
    }

    /// <summary>用一瓶药水（战斗内走动作队列；非战斗走模型层入口）。</summary>
    static bool UsePotionAt(object player, int slotIndex, int targetIdx)
    {
        bool ok = false;
        RunOnMain(new Action(() =>
        {
            try
            {
                List<PotionSlotInfo> slots = PotionSlots(player);
                PotionSlotInfo slot = null;
                for (int i = 0; i < slots.Count; i++) if (slots[i].Index == slotIndex) { slot = slots[i]; break; }
                if (slot == null || slot.Potion == null) { W("喝药水: 槽位 " + slotIndex + " 是空的"); return; }

                string why;
                if (!PotionUsable(slot.Potion, out why)) { W("喝药水: 「" + slot.Title + "」" + why); return; }

                object state = StateNow();
                bool inCombat = state != null && (RoomTypeText(state) ?? "").IndexOf("Monster", StringComparison.OrdinalIgnoreCase) >= 0;

                if (inCombat)
                {
                    object target = null;
                    string tt = S(Prop(slot.Potion, "TargetType"));
                    if (tt.IndexOf("Enemy", StringComparison.Ordinal) >= 0)
                    {
                        List<object> enemies = L(Prop(state, "Enemies"));
                        if (enemies.Count > 0)
                            target = (targetIdx >= 0 && targetIdx < enemies.Count) ? enemies[targetIdx] : enemies[0];
                    }
                    Type t = _sts2.GetType("MegaCrit.Sts2.Core.GameActions.UsePotionAction", false);
                    if (t == null) { W("喝药水: 找不到 UsePotionAction"); return; }
                    object action = Activator.CreateInstance(t, new object[] { slot.Potion, target, true });
                    object sync = Sync();
                    if (sync == null) { W("喝药水: 没有动作队列"); return; }
                    MethodInfo req = null;
                    foreach (MethodInfo m in sync.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        if (m.Name == "RequestEnqueue" && m.GetParameters().Length == 1) { req = m; break; }
                    if (req == null) { W("喝药水: 找不到 RequestEnqueue"); return; }
                    req.Invoke(sync, new object[] { action });
                    W("喝药水 → 入队「" + slot.Title + "」" + (target != null ? " → " + S(Prop(target, "Name")) : ""));
                    ok = true;
                }
                else
                {
                    MethodInfo enq = slot.Potion.GetType().GetMethod("EnqueueManualUse", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (enq == null) { W("喝药水: 找不到 EnqueueManualUse"); return; }
                    enq.Invoke(slot.Potion, new object[] { null });
                    W("喝药水（非战斗）→ 「" + slot.Title + "」");
                    ok = true;
                }
            }
            catch (Exception e)
            {
                Exception ie = e.InnerException != null ? e.InnerException : e;
                W("喝药水失败: " + ie.GetType().Name + ": " + ie.Message);
            }
        }));
        return ok;
    }

    /// <summary>丢弃一瓶药水（PotionCmd.Discard 是游戏自己的入口）。</summary>
    static bool DiscardPotionAt(object player, int slotIndex)
    {
        bool ok = false;
        RunOnMain(new Action(() =>
        {
            try
            {
                List<PotionSlotInfo> slots = PotionSlots(player);
                PotionSlotInfo slot = null;
                for (int i = 0; i < slots.Count; i++) if (slots[i].Index == slotIndex) { slot = slots[i]; break; }
                if (slot == null || slot.Potion == null) { W("丢药水: 槽位是空的"); return; }

                Type pc = _sts2.GetType("MegaCrit.Sts2.Core.Commands.PotionCmd", false);
                MethodInfo discard = null;
                if (pc != null)
                    foreach (MethodInfo m in pc.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        if (m.Name == "Discard" && m.GetParameters().Length == 1) { discard = m; break; }
                if (discard == null) { W("丢药水: 找不到 PotionCmd.Discard"); return; }
                discard.Invoke(null, new object[] { slot.Potion });
                W("丢药水 → 「" + slot.Title + "」");
                ok = true;
            }
            catch (Exception e)
            {
                Exception ie = e.InnerException != null ? e.InnerException : e;
                W("丢药水失败: " + ie.GetType().Name + ": " + ie.Message);
            }
        }));
        return ok;
    }

    // 把药水塞进战斗 payload：给模型看的候选
    static void AppendPotionsJson(StringBuilder sb, object player)
    {
        try
        {
            List<PotionSlotInfo> slots = PotionSlots(player);
            if (slots.Count == 0) return;
            sb.Append(",\"potions\":[");
            bool first = true;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].Potion == null) continue;
                if (!first) sb.Append(',');
                first = false;
                string why;
                bool usable = PotionUsable(slots[i].Potion, out why);
                sb.Append("{\"i\":").Append(slots[i].Index)
                  .Append(",\"name\":\"").Append(J(slots[i].Title)).Append('"');

                if (!usable) sb.Append(",\"usable\":false");
                sb.Append('}');
            }
            sb.Append(']');
        }
        catch { }
    }
}
