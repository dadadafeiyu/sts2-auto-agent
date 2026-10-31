# 架构：它是怎么跑起来的

> 一句话：**游戏内模组负责"读局面 + 执行动作"，本地决策服务负责"问模型"，模型只回一个 JSON。**

```
┌─────────────────────────────┐
│  Slay the Spire 2（Godot）   │
│  ┌───────────────────────┐   │
│  │  AgentSuite 模组      │   │
│  │  ① 读局面（只读）      │   │
│  │  ② 组 payload         │   │
│  │  ③ POST → 决策服务     │   │
│  │  ④ 执行 JSON 里的动作   │   │
│  └───────────────────────┘   │
└──────────────┬──────────────┘
               │ HTTP 127.0.0.1:18731
┌──────────────▼──────────────┐
│  decider（Node.js）          │
│  提示词 + payload → 模型     │
│  模型回 JSON → 原样返回       │
└─────────────────────────────┘
```

**模型看不到画面，也不点坐标。** 它拿到的是结构化事实，回的是索引。

---

## 一、模组侧：五个入口

### 1. 战斗出牌（`RunTurn`）

游戏的 `CardCmd.AutoPlay` 会被 AutoSlay 调用，我们在**调用点**上把它改写成自己的 `HookAutoPlay`：

```csharp
// 编译期改写：call CardCmd.AutoPlay → call HookAutoPlay
// 战斗循环因此走到我们的 RunTurn
```

`RunTurn` 每一拍做四件事：

```
① 组 payload（手牌 / 敌人 / 能量 / 威胁 / 药水）
② 问模型            → 得到 {"i":手牌索引,"t":目标索引} 或 {"end":true}
③ 执行              → new PlayCardAction(card, target) → RequestEnqueue
④ 等结算            → 手牌数变化 / 能量变化 才进下一拍
```

**出牌前有预检**：`CardModel.CanPlay(out reason)`，打不出去直接拒绝并写日志，不让它静默丢弃。

### 2. 卡牌选择（自己的 `ICardSelector`）

这是整个项目最关键的一处。

```csharp
CardSelectCmd.PushSelector(new AgentCardSelector());
```

挂上之后**选牌界面不再弹出**，游戏直接问我们要答案。一次覆盖 10 个场景（奖励/锻造/附魔/删除/变形/弃牌/网格…）。

**但接口只给 `(cards, min, max)`，没有 prompt、没有用途。** 所以用途靠两条路推断：

| 手段 | 说明 |
|---|---|
| **栈**（首选） | `StackTrace` 里找 `<FromDeckForUpgrade>d__N` 这种 async 状态机类型名 |
| **入口钩子**（兜底） | 给 12 个 `CardSelectCmd.From*` 状态机的 `MoveNext` 挂前缀，读它的 `<prefs>` 字段（`CardSelectorPrefs` 自带 `RemoveSelectionPrompt` 等专用提示） |

**判定结果决定方向**：

```
upgrade / enchant / reward  → pick = highest（挑最好的）
remove / transform / discard → pick = lowest（挑最差的）
simple（判不出）            → 不给方向，让模型自己看
```

> 踩过的坑：商店买"删牌服务"走的是 `FromDeckGeneric`，名字里没有方向。
> 一开始按 highest 处理，结果删掉了最好的牌（中和）。

### 3. 房间决策（点选项时拦截）

事件 / 营火 / 商店的按钮被 `NClickableControl.ForceClick` 拦截：

```
有人要点选项 → 我们收集所有选项 → 问模型 → 点它选的那个
```

营火有额外评分（`RestAdvice`）：血量 <25% 建议休息，25~50% 同分（交给她权衡），>50% 建议锻造。

### 4. 地图选路（点节点时拦截）

所有 `NMapPoint` 的点击被拦下 → 收集可走节点 + 路线评分 → 问模型 → 点它选的。

### 5. 开局（`OurMainMenuAsync`）

接管 `AutoSlay.PlayMainMenuAsync`：有存档就继续，没有就按角色文件/模型选择开局。

---

## 二、数值：全部让游戏自己算

**这是本项目和"自己写公式"最大的区别。**

| 数据 | 来源 | 说明 |
|---|---|---|
| 卡牌伤害 | `DamageVar.UpdateCardPreview(...)` → `PreviewValue` | 内部走 `Hook.ModifyDamage`，含力量/附魔/精准等全部修正 |
| 卡牌格挡 | `BlockVar.UpdateCardPreview(...)` → `PreviewValue` | 内部走 `Hook.ModifyBlock`，含敏捷 |
| 卡牌费用 | `EnergyCost.GetAmountToSpend()` | 含临时增减费 |
| 敌人意图 | `AttackIntent.GetTotalDamage(targets, attacker)` | **UI 显示用的就是它** |
| 毒伤 | `n·p − n(n−1)/2`（n = 1+触媒，且 n ≤ 毒层） | 毒每次触发掉当前层数并 -1，触媒每层多触发一次 |

**为什么不用自己的公式**：我们试过 `基础值 + 力量 × 易伤 × 虚弱`，结果漏掉"精准"这类改写器，小刀伤害永远偏低；而且我们自己算的两处数字（intent 与 threat）还会互相打架。

> 一个真实的坑：`PreviewCardVar` 传 `target = null` 得到的是"不对着具体敌人"的数值（含力量、不含目标易伤）。
> 这是故意的——目标易伤由模型结合 `enemies[].powers` 自己乘。

---

## 三、payload 长什么样

**战斗（`combat_turn`）**

```json
{
  "id": 42, "kind": "combat_turn", "char": "silent",
  "energy": 3, "turn": 2, "hp": 61, "maxHp": 70, "block": 0,
  "handComplete": false,
  "powers": [{"n": "力量", "a": 3, "d": "攻击伤害提高3点。"}],
  "threat": "将受到 15 点伤害，现有护甲 0，还需挡 15 点",
  "hand": [
    {"i":0,"title":"打击","cost":"1","target":"AnyEnemy","desc":"造成6点伤害。","dmg":9,"hits":1},
    {"i":1,"title":"中和","cost":"0","target":"AnyEnemy","desc":"造成3点伤害。 给予1层虚弱。","dmg":3,"hits":1}
  ],
  "enemies": [
    {"i":0,"name":"缩小甲虫","hp":38,"block":0,
     "intent":{"type":"attack","total":15,"perHit":7,"hits":1,"afterBlock":15,"iAmVulnerable":true},
     "poison":{"stacks":6,"catalyst":1,"endTurnDamage":11},
     "killGain":"打掉它需 38 点伤害，之后少挨 15 点",
     "powers":[{"n":"中毒","a":6}]}
  ]
}
```

**选牌（`pick_cards`）**

```json
{"kind":"pick_cards","min":1,"max":1,"purpose":"remove","pick":"lowest",
 "options":[{"i":0,"title":"打击","score":50,"tier":"C"},
            {"i":1,"title":"中和","score":65,"tier":"B"}]}
```

**商店 / 事件 / 营火** 都是 `pick_option` / `pick_shop` 同一套：给候选 + 分数 + 理由，模型回 `{"index":n}`。

**关于字段的取舍**（为什么没有 `vars`）：

```
卡牌的动态变量（虚弱层数、抽牌数、生成数、重复次数）在拼 desc 时就被填进去了：
  "获得把小型刀刃" → "获得2把小型刀刃"
描述里没出现的变量，再追加成 "（另有 Repeat=2）"

所以 payload 里不再单发 vars —— 数字都在人话里，模型少一层"去 vars 找数"的动作。
同理：
  · powers 只给 {"n":"力量","a":3}，不发解释文字
  · 药水只给名字（而且药水功能目前是坏的）
```

---

## 四、健康度保障

| 机制 | 作用 |
|---|---|
| **放宽 handler 超时**（8 个 → 30 分钟） | AutoSlay 默认战斗房 5 分钟，我们"每张牌问一次模型"必然超时 |
| **selector 冲突处理** | `UseSelector` 是独占语义，必须等 AutoSlay 先装完，我们再 `PushSelector` 压栈 |
| **JSON 自检** | 发送前校验 payload，坏了当场报，而不是等插件报 `Expected ','` |
| **手牌稳定性**（`handComplete`） | 发牌动画没播完时手牌是不全的，标出来让模型知道 |
| **回合结束只拦一次** | 有能量 + 有能打的牌时提醒一次；有冰淇淋不拦（允许留费） |

---

## 五、为什么还是离不开 AutoSlay

**我们只接管了"决策点"，没接管"什么时候做什么"。**

```
AutoSlay 决定：进房间 → 打开界面 → 点继续 → 下一层
我们决定：     选哪个牌 / 打哪张牌 / 走哪条路
```

自己写主循环试过一版，卡在"读不到事件房节点"（场景树里找不到），暂缓。

**代价**：游戏更新改 AutoSlay → 本模组失效（日志会出现「找不到 PlayRunAsync」）。

---

## 六、文件对应

| 文件 | 职责 |
|---|---|
| `AgentSeat.cs` | 主体：战斗、房间、地图、开局、payload 组装 |
| `AgentSeat.Selector.cs` | 自己的 `ICardSelector` + 用途判定 + 选牌 payload |
| `AgentSeat.Potions.cs` | 药水（**失败**，见 README） |
| `ClickSeat.cs` | 选项/地图节点点击拦截 + 路线评分 |
| `Knowledge.cs` | 知识库加载（评分/升级分/词条）+ 动态调分 |
| `KnowledgeExport.cs` | 从游戏导出 cards/relics/potions/powers/events |
| `Paths.cs` | 所有外部路径（配置 > 模组目录 > %APPDATA%） |
| `decider/index.js` | 本地 HTTP 服务，拼提示词调模型 |
| `decider/llm.js` | 模型调用分派（宿主优先 / 自带 shim） |
