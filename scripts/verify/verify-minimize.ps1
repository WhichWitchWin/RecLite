# Temporary verification (Chinese strings -> UTF-8 with BOM):
# 1) minimizing keeps the window in the taskbar (not hidden), 2) second launch restores it.
$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class MinIn {
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
"@

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe  = Join-Path $root 'out\RecLite.exe'
$run  = Join-Path $env:TEMP ('RecLiteMin\' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
Copy-Item $exe (Join-Path $run 'RecLite.exe') -Force
$appExe = Join-Path $run 'RecLite.exe'

try {
    Get-Process RecLite -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800

    Write-Host 'start A minimized'
    $a = Start-Process -FilePath $appExe -WorkingDirectory $run -PassThru -WindowStyle Minimized
    Start-Sleep -Seconds 6
    $a.Refresh()
    $hwndA = $a.MainWindowHandle
    Write-Host ('A alive=' + (-not $a.HasExited))
    Write-Host ('A main window handle = ' + $hwndA + '  (must be non-zero => taskbar button exists)')
    if ($hwndA -ne 0) {
        Write-Host ('A window visible = ' + [MinIn]::IsWindowVisible($hwndA) + ' | minimized = ' + [MinIn]::IsIconic($hwndA))
    }

    $log = Join-Path $run 'RecLite.log'
    $text = ''
    if (Test-Path $log) { $text = [System.IO.File]::ReadAllText($log, [System.Text.Encoding]::UTF8) }
    Write-Host ('log has tray errors : ' + ($text -match 'InitTray|Tray\.(Show|LoadIcon|TaskbarWatcher)'))
    Write-Host ('log has hide-to-tray: ' + ($text -match '窗口最小化到托盘'))

    Write-Host 'start B (should restore A, then exit)'
    $b = Start-Process -FilePath $appExe -WorkingDirectory $run -PassThru
    Start-Sleep -Seconds 4
    $b.Refresh()
    Write-Host ('B exited = ' + $b.HasExited)

    Start-Sleep -Seconds 2
    $restored = $false
    if ($hwndA -ne 0) { $restored = -not [MinIn]::IsIconic($hwndA) }
    Write-Host ('A restored (not minimized) = ' + $restored)

    $text2 = ''
    if (Test-Path $log) { $text2 = [System.IO.File]::ReadAllText($log, [System.Text.Encoding]::UTF8) }
    Write-Host ('log restore line = ' + ($text2 -match '恢复窗口（托盘图标或第二次启动）'))

    $taskbarOk = ($hwndA -ne 0)
    $notHidden = -not ($text -match '窗口最小化到托盘')
    $ok = $taskbarOk -and $notHidden -and $b.HasExited -and $restored -and ($text2 -match '恢复窗口')
    Write-Host ('RESULT: ' + $(if ($ok) { 'PASS - minimize keeps taskbar entry; second launch restores' } else { 'FAIL' }))
}
finally {
    Get-Process RecLite -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Host ('run dir: ' + $run)
}
