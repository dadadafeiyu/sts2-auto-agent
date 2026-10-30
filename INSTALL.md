# 安装说明（写给 AI 看的）

> 目标：让 AI 读完这份文档，就能在一台干净 Windows 机器上把它装好并验证。
> 每一步都有**可执行的验证命令**，验证不过别往下走。

---

## 0. 前置检查

```powershell
# 游戏在不在（路径可以换成你的）
Test-Path "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll"
# 期望：True

# 编译器（Windows 自带，不用装）
Test-Path "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
# 期望：True

# Node（决策服务用，18+）
node -v
```

**任一不满足就停下**，不要跳过。

---

## 1. 编译 + 安装模组

```powershell
cd <本仓库根目录>
powershell -File mod\build.ps1 -Install
```

脚本会自己找游戏目录（Steam 库 / 常见路径）。找不到就手动指定：

```powershell
powershell -File mod\build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Slay the Spire 2" -Install
```

**验证：**

```powershell
Test-Path "<游戏目录>\mods\AgentSuite\AgentSuite.dll"        # 期望 True
Test-Path "<游戏目录>\mods\AgentSuite\knowledge\scores.tsv"  # 期望 True
```

> **如果第二个是 False**：知识库没装进去 → 牌会全部按默认分（40）处理，行为会很差但不报错。
> 手动补：把 `mod-assets\knowledge\` 整个复制到 `<游戏目录>\mods\AgentSuite\knowledge\`。

---

## 2. 起决策服务

```powershell
node decider\index.js
```

**验证：**

```powershell
(Invoke-WebRequest -Uri http://127.0.0.1:18731/decisions -TimeoutSec 3).StatusCode
# 期望：200（404 也说明服务活着，只是路由不同）
```

### 模型怎么接（二选一，服务自己会挑）

`decider/index.js` 从 `decider/llm.js` 拿模型调用，它会按顺序尝试：

1. **宿主实现**：`../../src/llm.js`（如果你在 DeepSeek Harness 这类宿主里跑）
2. **自带 shim**：`decider/llm-shim.js`（OpenAI 兼容接口，任何人可用）

启动时会打印用的是哪一个：

```
[sts2-decider] 模型调用来源: shim(OpenAI 兼容接口)
```

**用自带 shim 时，配三样东西（环境变量或 config.json 的 `llm` 段）：**

```powershell
$env:STS2_LLM_BASE_URL = "https://api.deepseek.com/v1"
$env:STS2_LLM_API_KEY  = "sk-..."
$env:STS2_LLM_MODEL    = "deepseek-chat"
node decider\index.js
```

或写进 `%APPDATA%\Sts2Agent\config.json`：

```json
{
  "port": 18731,
  "llm": { "baseUrl": "https://api.deepseek.com/v1", "apiKey": "sk-...", "model": "deepseek-chat" }
}
```

**验证模型真的通了：**

```powershell
# 造一个最小的选牌请求，看有没有返回 picks
$body = '{"id":1,"kind":"pick_cards","min":1,"max":1,"purpose":"reward","pick":"highest","options":[{"i":0,"title":"打击","score":20},{"i":1,"title":"后空翻","score":77}]}'
Invoke-RestMethod -Uri http://127.0.0.1:18731/decide -Method Post -Body $body -ContentType 'application/json'
# 期望：包含 picks 的 JSON（不是 error）
```

---

## 3. 让模组进入"自己玩"模式

模组默认是**手动模式**（不干预你玩）。要它接管，需要写一个 flag：

```powershell
$dir = Join-Path $env:APPDATA 'Sts2Agent'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Set-Content (Join-Path $dir 'autoplay.flag') ([DateTime]::Now.Ticks) -NoNewline
Set-Content (Join-Path $dir 'next-character.txt') 'silent' -NoNewline   # 角色：silent/ironclad/defect/regent，或 auto
```

> flag 有效期 5 分钟，模组读完会自己删掉。

---

## 4. 开游戏验证

1. 启动游戏
2. 看日志：

```powershell
Get-Content "$env:APPDATA\Sts2Agent\logs\agentseat.log" -Tail 20
```

**期望依次出现：**

```
选牌用途钩子已挂: 12 个入口
已放宽 8 个 handler 的超时 -> 30 分钟
Agent 模式：flag 已消费，开始接管
选牌 selector: 已压栈 → 栈顶是我们
```

3. 进一场战斗，日志应出现：

```
看局面: {"id":..,"kind":"combat_turn",...}
出牌 → 入队「打击」
```

**出现 `出牌 → 入队` 就说明整条链路通了。**

---

## 常见错误

| 现象 | 原因 | 处理 |
|---|---|---|
| `找不到游戏目录` | 游戏不在默认位置 | 用 `-GameDir` 指定 |
| `DLL 复制未生效` | 游戏还开着（文件被锁） | 关游戏再装 |
| 日志里分数全是 40 | 知识库没装 | 复制 `mod-assets\knowledge` 到 `mods\AgentSuite\` |
| `找不到 PlayRunAsync` | 游戏更新改了 AutoSlay | 本项目短期内失效，需要重新适配 |
| 决策一直超时 | 决策服务没起 / 端口不对 | 确认 `node decider\index.js` 在跑 |
| 卡牌伤害明显偏低 | 知识库版本旧 | 用模组的导出器重新生成 `cards.json` |
| `A card selector is already active` | 别的模组也接管了选牌 | 只保留一个 |

---

## 可选：QQ 播报

见 `integrations/qq/README.md`。**不装也能玩**，只是没有聊天播报和局末总结。
