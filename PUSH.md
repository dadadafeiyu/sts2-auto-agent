# 怎么把它推到 GitHub / Gitee

> 你只需要成功推一次（包只有 1 MB）。

## 一、先让网络能到 GitHub（Gitee 不需要这一步）

**推荐：Watt Toolkit（原名 Steam++）**

1. 下载 https://steampp.net
2. 安装后打开 → 左侧「网络加速」→ 勾选 **GitHub**
3. 点「一键加速」，等它显示已生效

验证：

```powershell
curl.exe -I https://github.com
# 期望：HTTP/2 200 或 301
```

## 二、装 Git（只需一次）

```
https://git-scm.com/download/win          （官方，可能慢）
https://registry.npmmirror.com/-/binary/git-for-windows/   （国内镜像）
```

装完重开 PowerShell，验证：

```powershell
git --version
```

## 三、建仓库

**GitHub**

```
右上角 + → New repository → 名字 sts2-auto-agent → Public
不要勾 Add README（否则会多一个提交）
```

**Gitee**

```
右上角 + → 新建仓库 → 路径用英文 sts2-auto-agent → 开源
不要勾"初始化仓库"
```

## 四、推（一条命令）

```powershell
cd "E:\哦鲸鲸爬塔2"

# GitHub
powershell -File push.ps1 -Repo "https://github.com/你的用户名/sts2-auto-agent.git"

# 或 Gitee
powershell -File push.ps1 -Repo "https://gitee.com/你的用户名/sts2-auto-agent.git"
```

**第一次会要账号密码：**

| 平台 | 用什么 |
|---|---|
| Gitee | 账号 + 密码 |
| **GitHub** | **不能用密码**，要用 Personal Access Token（见下） |

## GitHub 的 Token 怎么拿

```
GitHub → 右上头像 → Settings → Developer settings
→ Personal access tokens → Tokens (classic) → Generate new token
→ 勾选 repo → 生成 → 复制那串 ghp_xxx
```

**推送时：用户名填 GitHub 用户名，密码位置粘 token。**

## 推不动怎么办

| 现象 | 处理 |
|---|---|
| `Failed to connect to github.com` | 网络没通 → 开 Watt Toolkit |
| `Authentication failed` | GitHub 用了密码 → 换成 token |
| `remote: Repository not found` | 仓库没建，或地址拼错 |
| `src refspec main does not match any` | 没提交成功 → 先 `git add . && git commit -m "init"` |

## 如果 GitHub 死活不通

**直接放弃，用 Gitee**（国内直连，流程一模一样，只换地址）。

或者干脆**发压缩包**：把 `E:\哦鲸鲸爬塔2` 打包成 zip，别人解压后照 `INSTALL.md` 装。
