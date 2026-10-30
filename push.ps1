param(
  [Parameter(Mandatory=$false)][string]$Repo = '',        # 例如 https://github.com/你的名字/sts2-auto-agent.git
  [string]$Branch = 'main',
  [string]$Message = '初始版本：让 AI 自己打杀戮尖塔 2'
)

$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot

# 0. 前置：git 在不在
$git = (Get-Command git -ErrorAction SilentlyContinue)
if (-not $git) {
  Write-Host "没装 git。先装：https://git-scm.com/download/win" -ForegroundColor Yellow
  Write-Host "国内镜像：https://registry.npmmirror.com/-/binary/git-for-windows/" -ForegroundColor Yellow
  exit 1
}

if (-not $Repo) {
  Write-Host "用法：powershell -File push.ps1 -Repo https://github.com/你的名字/sts2-auto-agent.git"
  Write-Host "（Gitee 就把地址换成 https://gitee.com/你的名字/sts2-auto-agent.git）"
  exit 1
}

# 1. 初始化（只第一次）
if (-not (Test-Path (Join-Path $PSScriptRoot '.git'))) {
  git init
  Write-Host "已初始化仓库" -ForegroundColor Green
}

# 2. 提交
git add .
git -c user.name="sts2-auto-agent" -c user.email="noreply@example.com" commit -m $Message
if ($LASTEXITCODE -ne 0) { Write-Host "没有新改动，跳过提交" -ForegroundColor Yellow }

# 3. 远端
$hasOrigin = (git remote) -contains 'origin'
if ($hasOrigin) { git remote set-url origin $Repo } else { git remote add origin $Repo }

# 4. 推
git branch -M $Branch
Write-Host "开始推送（第一次会让你输账号密码）：$Repo" -ForegroundColor Cyan
git push -u origin $Branch

if ($LASTEXITCODE -eq 0) {
  Write-Host "推送成功 🎉" -ForegroundColor Green
} else {
  Write-Host "推送失败。常见原因：" -ForegroundColor Red
  Write-Host "  - 网络到不了 GitHub → 先开 Watt Toolkit 加速"
  Write-Host "  - 仓库地址写错 / 没建仓库"
  Write-Host "  - 账号密码错（Gitee 用账号密码，GitHub 用 Personal Access Token）"
}
