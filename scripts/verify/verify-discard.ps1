# Temporary verification script (Chinese strings -> must stay UTF-8 with BOM)
# Verifies: pressing "■ 丢弃" stops recording and leaves NO saved file behind.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe  = Join-Path $root 'out\RecLite.exe'
$run  = Join-Path $env:TEMP ('RecLiteDiscard\' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
Copy-Item $exe (Join-Path $run 'RecLite.exe') -Force

try {
    Get-Process RecLite -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    $proc = Start-Process -FilePath (Join-Path $run 'RecLite.exe') -WorkingDirectory $run -PassThru -WindowStyle Minimized -ArgumentList '--no-tray'

    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $win = $null
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
    if (-not $win) { throw 'main window did not become ready' }

    function ById($id) {
        return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
    }
    function WaitId($id, [int]$t = 12) {
        $d = (Get-Date).AddSeconds($t)
        while ((Get-Date) -lt $d) { $e = ById $id; if ($e) { return $e }; Start-Sleep -Milliseconds 300 }
        return $null
    }
    function Click($id) {
        $el = WaitId $id 12
        if (-not $el) { throw "control not found: $id" }
        $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    function Text($id) { $e = WaitId $id 8; if ($e) { return $e.Current.Name } else { return '' } }

    Write-Host 'waiting 8s for preroll buffer'
    Start-Sleep -Seconds 8

    Click 'RecordBtn'
    Write-Host ('recording timer: ' + (Text 'TimerText'))
    Start-Sleep -Seconds 3

    $dis = WaitId 'RecordBtnDiscard' 5
    if (-not $dis) { throw 'RecordBtnDiscard not found' }
    Write-Host ('discard button: "' + $dis.Current.Name + '" offscreen=' + $dis.Current.IsOffscreen)

    Click 'RecordBtnDiscard'
    Start-Sleep -Seconds 4
    $st = Text 'StatusText'
    Write-Host ('status: ' + $st)

    $dir = Join-Path $run 'Recordings'
    $wavs = @()
    if (Test-Path $dir) { $wavs = @(Get-ChildItem $dir -Filter *.wav -ErrorAction SilentlyContinue) }
    Write-Host ('wav files left: ' + $wavs.Count)

    $log = Join-Path $run 'RecLite.log'
    $logText = ''
    if (Test-Path $log) { $logText = [System.IO.File]::ReadAllText($log, [System.Text.Encoding]::UTF8) }
    $discardLogged = $logText -match '丢弃录音'
    Write-Host ('log has discard line: ' + $discardLogged)

    $ok = ($st -like '*已丢弃*') -and ($wavs.Count -eq 0) -and $discardLogged
    Write-Host ('RESULT: ' + $(if ($ok) { 'PASS - discarded, nothing saved' } else { 'FAIL' }))
}
finally {
    Get-Process RecLite -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Host ('run dir: ' + $run)
}
