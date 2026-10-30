param(
  [string]$GameDir = '',
  [string]$Mod = 'AgentSuite',
  [switch]$Install
)

$ErrorActionPreference = 'Continue'

# -- 1. 找游戏目录：参数 > 环境变量 > Steam 库 > 常见默认路径 --------------
function Find-Game {
  param([string]$Hint)
  $cands = New-Object System.Collections.ArrayList

  if ($Hint) { [void]$cands.Add($Hint) }
  if ($env:STS2_GAME_DIR) { [void]$cands.Add($env:STS2_GAME_DIR) }

  $roots = @()
  try { if ($env:ProgramFiles) { $roots += (Join-Path $env:ProgramFiles 'Steam') } } catch { }
  try { if (${env:ProgramFiles(x86)}) { $roots += (Join-Path ${env:ProgramFiles(x86)} 'Steam') } } catch { }
  $roots += 'C:\Program Files (x86)\Steam'
  $roots += 'C:\Program Files\Steam'
  $roots += 'D:\Steam','D:\SteamLibrary','E:\Steam','E:\SteamLibrary','F:\Steam','F:\SteamLibrary','G:\Steam'

  foreach ($root in $roots) {
    try {
      if (-not $root) { continue }
      if (-not (Test-Path $root)) { continue }
      $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
      if (Test-Path $vdf) {
        foreach ($line in Get-Content $vdf) {
          $m = [regex]::Match($line, '"path"\s+"([^"]+)"')
          if ($m.Success) {
            $pp = $m.Groups[1].Value -replace '\\\\', '\'
            if (Test-Path $pp) { [void]$cands.Add((Join-Path $pp 'steamapps\common\Slay the Spire 2')) }
          }
        }
      }
      [void]$cands.Add((Join-Path $root 'steamapps\common\Slay the Spire 2'))
    } catch { }
  }

  # 每个盘符下的常见布局 + 一层目录扫描（能抓到 E:\sbeam 这种自定义 Steam 库）
  foreach ($d in 'C','D','E','F','G') {
    try {
      if (-not (Test-Path ($d + ':\'))) { continue }
      [void]$cands.Add(($d + ':\steamapps\common\Slay the Spire 2'))
      [void]$cands.Add(($d + ':\SteamLibrary\steamapps\common\Slay the Spire 2'))
      [void]$cands.Add(($d + ':\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2'))
      [void]$cands.Add(($d + ':\Games\Steam\steamapps\common\Slay the Spire 2'))
      Get-ChildItem -Path ($d + ':\') -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        [void]$cands.Add((Join-Path $_.FullName 'steamapps\common\Slay the Spire 2'))
      }
    } catch { }
  }

  foreach ($c in $cands) {
    try {
      if (-not $c) { continue }
      if (Test-Path (Join-Path $c 'data_sts2_windows_x86_64\sts2.dll')) { return $c }
    } catch { }
  }
  return $null
}

if (-not $GameDir) { $GameDir = Find-Game }
if (-not $GameDir) {
  Write-Host "找不到游戏目录。请指定：" -ForegroundColor Red
  Write-Host "  powershell -File build.ps1 -GameDir `"<Slay the Spire 2 路径>`" -Install"
  Write-Host "或设置环境变量 STS2_GAME_DIR"
  exit 1
}

$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$data = Join-Path $GameDir 'data_sts2_windows_x86_64'
$out  = Join-Path $PSScriptRoot 'out'
if (-not (Test-Path $out)) { New-Item -ItemType Directory -Path $out | Out-Null }

Write-Host "游戏目录: $GameDir"
Write-Host "编译器  : $csc"
if (-not (Test-Path $csc)) {
  Write-Host "找不到 csc.exe（需要 .NET Framework 4.x，Windows 自带）" -ForegroundColor Red
  exit 1
}

# -- 2. 源码目录（优先 ./src，兼容旧布局 ./AgentSuite/*.cs）----------------
$srcDir = Join-Path $PSScriptRoot 'src'
if (-not (Test-Path $srcDir)) { $srcDir = Join-Path $PSScriptRoot $Mod }
$srcFiles = @(Get-ChildItem $srcDir -Filter *.cs | ForEach-Object { $_.FullName })
if ($srcFiles.Count -eq 0) { Write-Host "找不到 .cs 源码: $srcDir" -ForegroundColor Red; exit 1 }

$dll = Join-Path $out ($Mod + '.dll')

$coreRefs = @(
  'mscorlib.dll','System.Private.CoreLib.dll','System.Runtime.dll','System.Reflection.Primitives.dll',
  'netstandard.dll','System.Collections.dll','System.IO.FileSystem.dll','System.Threading.dll',
  'System.Threading.Tasks.dll','System.Net.Http.dll','System.Net.Primitives.dll','System.Private.Uri.dll',
  'sts2.dll','0Harmony.dll','GodotSharp.dll'
) | ForEach-Object { '/r:' + (Join-Path $data $_) }

$cscArgs = @(
  '/nologo','/target:library','/langversion:5','/nostdlib+','/optimize+','/debug-',
  ('/out:' + $dll)
) + $coreRefs + $srcFiles

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { Write-Host "编译失败" -ForegroundColor Red; exit $LASTEXITCODE }

if ($Install) {
  $dest = Join-Path $GameDir ('mods\' + $Mod)
  if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest | Out-Null }
  try { Copy-Item $dll (Join-Path $dest ($Mod + '.dll')) -Force }
  catch { Write-Host "复制 DLL 失败（游戏可能还开着）" -ForegroundColor Red; exit 1 }

  $jsonSrc = Join-Path $srcDir ($Mod + '.json')
  if (-not (Test-Path $jsonSrc)) { $jsonSrc = Join-Path $PSScriptRoot ($Mod + '.json') }
  if (Test-Path $jsonSrc) { Copy-Item $jsonSrc (Join-Path $dest ($Mod + '.json')) -Force }

  # 知识库：跟 DLL 一起装到 mods\AgentSuite\knowledge
  $kbSrc = Join-Path $PSScriptRoot '..\mod-assets\knowledge'
  if (-not (Test-Path $kbSrc)) { $kbSrc = Join-Path $PSScriptRoot 'knowledge' }
  if (Test-Path $kbSrc) {
    $kbDest = Join-Path $dest 'knowledge'
    if (-not (Test-Path $kbDest)) { New-Item -ItemType Directory -Path $kbDest | Out-Null }
    Copy-Item (Join-Path $kbSrc '*') $kbDest -Recurse -Force
    Write-Host "知识库 -> $kbDest"
  } else {
    Write-Host "没找到知识库目录（mod-assets\knowledge），分数会全部走默认值" -ForegroundColor Yellow
  }

  # 校验复制真的生效（游戏运行中文件被锁会静默失败）
  $copy = Get-Item (Join-Path $dest ($Mod + '.dll')) -ErrorAction SilentlyContinue
  if (-not $copy -or $copy.LastWriteTime -lt (Get-Item $dll).LastWriteTime.AddSeconds(-2)) {
    Write-Host "DLL 复制未生效 —— 先关掉游戏再装" -ForegroundColor Red
    exit 1
  }
  Write-Host "已安装 -> $dest" -ForegroundColor Green
  Get-ChildItem $dest | Select-Object Name, Length | Format-Table -AutoSize
}
