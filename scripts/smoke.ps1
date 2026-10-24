# RecLite 冒烟验证脚本
#
# 作用：改完代码后一条命令确认「没把主流程改坏」。
#       它会编译 → 在一个干净的临时目录里启动 → 录音 / 停止保存 / 存预录 / 试听
#       → 检查运行日志里没有错误 → 收尾。
#
# 用法（位置：scripts\smoke.ps1，在任意目录执行都可以）：
#   .\smoke.ps1                  编译 Debug 版并验证
#   .\smoke.ps1 -NoBuild         跳过编译，直接验证 out\RecLite.exe
#   .\smoke.ps1 -Keep            把录音产物与日志复制到 scripts\smoke-artifacts
#
# 为什么用 Debug：Release 会覆盖 out\ 里正在用的那份，冒烟失败时不好回退。
# 想验证发布版就先用 .\build.ps1 出 Release，再跑 .\smoke.ps1 -NoBuild。
#
# 退出码：0 = 全部通过，1 = 有失败项

param(
    [switch]$NoBuild,
    [switch]$Keep
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

# 本脚本位于 scripts\smoke.ps1：往上一级就是仓库根
$tools = Split-Path -Parent $MyInvocation.MyCommand.Path
$root  = Split-Path -Parent $tools
$out   = Join-Path $root "out"
$exe   = Join-Path $out "RecLite.exe"
$art   = Join-Path $tools "smoke-artifacts"
$run   = Join-Path $env:TEMP ("RecLiteSmoke\" + (Get-Date -Format "yyyyMMdd_HHmmss"))

$script:fail = @()
function Step($name) { Write-Host ""; Write-Host "== $name ==" -ForegroundColor Cyan }
function OK($msg)     { Write-Host "   PASS  $msg" -ForegroundColor Green }
function Bad($msg)    { Write-Host "   FAIL  $msg" -ForegroundColor Red; $script:fail += $msg }
function Info($msg)   { Write-Host "         $msg" -ForegroundColor DarkGray }

try {
    # ---------------------------------------------------------------- 1 编译
    if (-not $NoBuild) {
        Step "1/6 编译（Debug）"
        $bp = Join-Path $tools "build.ps1"
        & $bp -Configuration Debug | Out-Null
        if (-not (Test-Path $exe)) { throw "编译后没找到 $exe" }
        OK "编译完成：$exe"
    } else {
        Step "1/6 编译（已跳过）"
        if (-not (Test-Path $exe)) { throw "out\RecLite.exe 不存在，请先运行 .\build.ps1" }
        Info "使用现有 $exe"
    }

    # ---------------------------------------------------------------- 2 干净目录启动
    Step "2/6 在工作目录启动（模拟全新安装）"
    New-Item -ItemType Directory -Path $run -Force | Out-Null
    Copy-Item $exe (Join-Path $run "RecLite.exe") -Force
    Info "运行目录：$run"

    Get-Process RecLite -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800

    # --no-tray：最小化启动时不缩进托盘，否则窗口一隐、下面的 UIA 就找不到控件
    $proc = Start-Process -FilePath (Join-Path $run "RecLite.exe") -WorkingDirectory $run -PassThru -WindowStyle Minimized -ArgumentList '--no-tray'
    $win = $null
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    # 注意：窗口刚出现时 UIA 只给 HWND 外壳、后代数为 0，约 2 秒后才给出 WPF 控件树。
    # 所以不能「匹配到窗口就算就绪」，必须等到后代数 > 0，否则后面找不到任何控件。
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        if ($proc.HasExited) { break }
        try {
            $rootEl = [System.Windows.Automation.AutomationElement]::RootElement
            foreach ($k in $rootEl.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
                if ($k.Current.Name -like 'RecLite*') {
                    $n = $k.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                        [System.Windows.Automation.Condition]::TrueCondition).Count
                    if ($n -gt 0) { $win = $k; break }
                }
            }
        } catch { }
        if ($win) { break }
    }
    if ($proc.HasExited) { throw ("程序启动后立即退出，退出码 0x{0:X8}" -f $proc.ExitCode) }
    if (-not $win) { throw "30 秒内没等到主窗口的控件树（UIA 只返回外壳）" }
    OK "主窗口已就绪"

    function ById($id) {
        return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
    }
    function WaitId($id, [int]$timeoutSec = 12) {
        $deadline = (Get-Date).AddSeconds($timeoutSec)
        while ((Get-Date) -lt $deadline) {
            $e = ById $id
            if ($e) { return $e }
            Start-Sleep -Milliseconds 300
        }
        return $null
    }
    function Click($id) {
        $el = WaitId $id 12
        if (-not $el) { throw "找不到控件 $id" }
        $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    function Text($id) { $e = WaitId $id 8; if ($e) { return $e.Current.Name } else { return "" } }

    $cap = WaitId 'CaptureCombo' 15
    if (-not $cap) { throw "15 秒内没出现录制通道下拉框" }
    # 设备是「窗口可见之后」才枚举的（启动提速的延后期），慢机器上可能先拿到空下拉框，
    # 所以这里最多重试 10 秒，而不是拿一次空值就判失败。
    $sel = $cap.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
    $capName = ''
    $deadline = (Get-Date).AddSeconds(10)
    while ([string]::IsNullOrWhiteSpace($capName) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
        try { $capName = ($sel.Current.GetSelection() | ForEach-Object { $_.Current.Name }) -join "," } catch { }
    }
    if ([string]::IsNullOrWhiteSpace($capName)) { throw "10 秒内录制通道一直是空的（设备可能没连上）" }
    OK "录制通道 = $capName"

    # ---------------------------------------------------------------- 3 等预录缓存填充
    Step "3/6 等待预录缓存填充（10 秒，供后面「存预录」使用）"
    Start-Sleep -Seconds 10
    OK ("计时器 = " + (Text 'TimerText'))

    # ---------------------------------------------------------------- 4 录音 → 停止保存
    Step "4/6 录音 3 秒 → 停止并保存（含预录）"
    Click 'RecordBtn'
    Start-Sleep -Seconds 3
    $t = Text 'TimerText'
    Info "录音中计时 = $t"
    Click 'RecordBtn'
    Start-Sleep -Seconds 5
    $st = Text 'StatusText'
    if ($st -like '*已保存*') { OK $st } else { Bad "停止后状态不对：$st" }

    # ---------------------------------------------------------------- 5 存预录 → 试听
    Step "5/6 存预录 → 试听"
    Click 'SavePrerollBtn'
    Start-Sleep -Seconds 5
    $st = Text 'StatusText'
    if ($st -like '*预录已保存*') { OK $st } else { Bad "存预录失败：$st" }

    Click 'PlayBtn'
    Start-Sleep -Seconds 2
    $st = Text 'StatusText'
    if ($st -like '*正在试听*') { OK $st } else { Bad "试听没起来：$st" }
    Click 'PlayBtn'
    Start-Sleep -Seconds 1
    Info ("停止试听后状态 = " + (Text 'StatusText'))

    # ---------------------------------------------------------------- 6 日志检查
    Step "6/6 检查运行日志"
    $log = Join-Path $run "RecLite.log"
    if (Test-Path $log) {
        $bad = Select-String -Path $log -Pattern '\[错误\]|Exception|UnhandledException' -Encoding UTF8
        if ($bad) {
            foreach ($b in $bad) { Bad ("日志异常：" + $b.Line.Trim()) }
        } else {
            OK "日志无错误"
        }
        $settings = Join-Path $run "settings.json"
        if (Test-Path $settings) {
            $json = Get-Content $settings -Raw -Encoding UTF8
            if ($json -match '"PrerollSeconds"\s*:\s*(\d+)') {
                Info ("全新安装预录秒数 = " + $matches[1])
            # 全新安装的音量必须是 100%：落到 0 就等于静音录制（曾经真的漏了默认值）
            foreach ($key in @('InputGain','PlaybackVolume')) {
                if ($json -match ('"' + $key + '"\s*:\s*([0-9.]+)')) {
                    $v = [double]$matches[1]
                    if ([math]::Abs($v - 1.0) -lt 0.001) { OK ("全新安装 $key = $v") }
                    else { Bad ("全新安装 $key 不是 100%：$v") }
                } else { Bad ("settings.json 里缺少 $key") }
            }
            }
        }
    } else {
        Bad "没有生成 RecLite.log"
    }
}
catch {
    Bad ("中断：" + $_.Exception.Message)
}
finally {
    Get-Process RecLite -ErrorAction SilentlyContinue | Stop-Process -Force

    if ($Keep -and (Test-Path $run)) {
        New-Item -ItemType Directory -Path $art -Force | Out-Null
        $stamp = Split-Path $run -Leaf
        Copy-Item $run (Join-Path $art $stamp) -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "   产物已复制到 $art\$stamp" -ForegroundColor DarkGray
    }

    Write-Host ""
    if ($script:fail.Count -eq 0) {
        Write-Host "冒烟结果：全部通过" -ForegroundColor Green
        exit 0
    } else {
        Write-Host ("冒烟结果：{0} 项失败" -f $script:fail.Count) -ForegroundColor Red
        foreach ($f in $script:fail) { Write-Host ("  - " + $f) -ForegroundColor Red }
        exit 1
    }
}
