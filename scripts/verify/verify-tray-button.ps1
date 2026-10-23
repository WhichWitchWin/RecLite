# Temporary verification (Chinese strings -> UTF-8 with BOM):
# 1) the dedicated "缩到托盘" button hides the window; 2) second launch brings it back.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe  = Join-Path $root 'out\RecLite.exe'
$run  = Join-Path $env:TEMP ('RecLiteTrayBtn\' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
Copy-Item $exe (Join-Path $run 'RecLite.exe') -Force
$appExe = Join-Path $run 'RecLite.exe'

try {
    Get-Process RecLite -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800

    Write-Host 'start A minimized'
    $a = Start-Process -FilePath $appExe -WorkingDirectory $run -PassThru -WindowStyle Minimized

    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $a.Id)
    $win = $null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        if ($a.HasExited) { break }
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
    if (-not $win) { throw 'main window did not become ready' }
    Write-Host 'window ready'

    function ById($id) {
        return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
    }

    $btn = $null
    for ($i = 0; $i -lt 20; $i++) { $btn = ById 'TrayBtn'; if ($btn) { break }; Start-Sleep -Milliseconds 300 }
    if (-not $btn) { throw 'TrayBtn not found' }
    Write-Host ('tray button: "' + $btn.Current.Name + '" enabled=' + $btn.Current.IsEnabled)

    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2
    $a.Refresh()
    Write-Host ('after click, A window handle = ' + $a.MainWindowHandle + ' (0 = hidden)')

    Write-Host 'start B (should restore A then exit)'
    $b = Start-Process -FilePath $appExe -WorkingDirectory $run -PassThru
    Start-Sleep -Seconds 4
    $b.Refresh()
    $a.Refresh()
    Write-Host ('B exited = ' + $b.HasExited)
    Write-Host ('A window handle now = ' + $a.MainWindowHandle + ' (non-zero = restored)')

    $log = Join-Path $run 'RecLite.log'
    $text = ''
    if (Test-Path $log) { $text = [System.IO.File]::ReadAllText($log, [System.Text.Encoding]::UTF8) }
    Write-Host ('log: tray button clicked = ' + ($text -match '点「缩到托盘」隐藏窗口'))
    Write-Host ('log: restored          = ' + ($text -match '恢复窗口'))
    Write-Host ('log: tray errors       = ' + ($text -match 'InitTray|Tray\.(Show|LoadIcon|TaskbarWatcher|Restore)'))

    $ok = $b.HasExited -and ($a.MainWindowHandle -ne 0) -and ($text -match '点「缩到托盘」隐藏窗口') -and ($text -match '恢复窗口')
    Write-Host ('RESULT: ' + $(if ($ok) { 'PASS - button hides to tray, second launch restores' } else { 'FAIL' }))
}
finally {
    Get-Process RecLite -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Host ('run dir: ' + $run)
}
