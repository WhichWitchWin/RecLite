# RecLite 构建脚本
#
# 用法（位置：scripts\build.ps1，在任意目录执行都可以）：
# 输出：out\RecLite.exe —— 本地测试和日常运行都用这一份，不再分 dist / release
#
#   .\build.ps1                          （默认 Release）
#   .\build.ps1 -Configuration Debug     （Debug 版，同样输出到 out\，覆盖前一份）
#   .\build.ps1 -Run                     （编译完成后顺手启动 out\ 里那版）
#
# 产物是「单文件 exe」：所有托管依赖（NAudio 系列等）和原生 sndfile.dll
# 都已经内嵌在 exe 里，所以复制发布时只需要那一个文件。

param(
    [string]$Configuration = "Release",
    [switch]$Run
)

$ErrorActionPreference = "Stop"

# 本脚本位于 scripts\build.ps1：往上一级就是仓库根（src\ 与 out\ 都在这一级）
$tools = Split-Path -Parent $MyInvocation.MyCommand.Path
$root  = Split-Path -Parent $tools
$src   = Join-Path $root "src"
$out   = Join-Path $root "out"

if (-not (Test-Path (Join-Path $src "RecLite.csproj"))) { throw "没找到 src\RecLite.csproj，请确认脚本位于仓库的 scripts\ 目录下" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "没找到 dotnet 命令，请先安装 .NET SDK 8" }

Write-Host "== 1/2 编译 ==" -ForegroundColor Cyan
Push-Location $src
try {
    & dotnet build -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "编译失败" }
} finally { Pop-Location }

$exe = Join-Path $src "bin\$Configuration\RecLite.exe"
if (-not (Test-Path $exe)) { throw "没找到编译产物：$exe" }

Write-Host "== 2/2 输出到 out ==" -ForegroundColor Cyan
if (-not (Test-Path $out)) { New-Item -ItemType Directory -Path $out | Out-Null }
Copy-Item $exe -Destination $out -Force

$f = Get-Item (Join-Path $out "RecLite.exe")
Write-Host ""
Write-Host ("输出完成：{0}" -f $f.FullName) -ForegroundColor Green
Write-Host ("大小：{0:N2} MB" -f ($f.Length / 1MB)) -ForegroundColor Green
if ($Run) {
    Write-Host "启动…" -ForegroundColor Cyan
    Get-Process RecLite -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    Start-Process -FilePath $f.FullName -WorkingDirectory $out
} else {
    Write-Host "单文件绿色版：复制这一个 exe 就能运行，录音会存在同级 Recordings 目录"
}
