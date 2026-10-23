# Temporary verification (Chinese strings -> UTF-8 with BOM):
# Start instance A minimized (tray), then start instance B: B must exit and A's window must come back.
$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class SIn {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string win);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
"@

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe  = Join-Path $root 'out\RecLite.exe'
$run  = Join-Path $env:TEMP ('RecLiteSingle\' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
Copy-Item $exe (Join-Path $run 'RecLite.exe') -Force
$appExe = Join-Path $run 'RecLite.exe'

try {
    Get-Process RecLite -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800

    Write-Host 'start A (minimized -> tray)'
    $a = Start-Process -FilePath $appExe -WorkingDirectory $run -PassThru -WindowStyle Minimized
    Start-Sleep -Seconds 6
    $a.Refresh()
    Write-Host ('A alive=' + (-not $a.HasExited) + ' | window handle=' + $a.MainWindowHandle)

    $hwnd = [SIn]::FindWindow($null, 'RecLite 轻录音')   # 隐藏时通常为 0，仅作参考
    Write-Host ('A hidden hwnd=' + $hwnd + ' | visible=' + [SIn]::IsWindowVisible($hwnd))

    Write-Host 'start B (normal start -> should restore A then exit)'
    $b = Start-Process -FilePath $appExe -WorkingDirectory $run -PassThru
    Start-Sleep -Seconds 4
    $b.Refresh()
    $bExited = $b.HasExited
    Write-Host ('B exited=' + $bExited)

    Start-Sleep -Seconds 2
    # 隐藏时 FindWindow 拿不到句柄，所以改用「A 的主窗口句柄是否非 0」判断是否已恢复
    $visibleNow = ($a.MainWindowHandle -ne 0)
    $a.Refresh()
    Write-Host ('A window visible now=' + $visibleNow + ' | handle=' + $a.MainWindowHandle)

    $log = Join-Path $run 'RecLite.log'
    $text = ''
    if (Test-Path $log) { $text = [System.IO.File]::ReadAllText($log, [System.Text.Encoding]::UTF8) }
    Write-Host ('log: requested restore  = ' + ($text -match '已请求它恢复窗口'))
    Write-Host ('log: restored from tray = ' + ($text -match '从托盘恢复窗口'))

    $ok = $bExited -and $visibleNow -and ($text -match '已请求它恢复窗口') -and ($text -match '从托盘恢复窗口')
    Write-Host ('RESULT: ' + $(if ($ok) { 'PASS - second launch restored the hidden window' } else { 'FAIL' }))
}
finally {
    Get-Process RecLite -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Host ('run dir: ' + $run)
}
