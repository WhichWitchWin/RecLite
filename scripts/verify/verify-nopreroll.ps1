# Temporary verification script (ASCII only, no BOM needed)
# Verifies: "Stop (without preroll)" trims the preroll segment from the saved file.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe  = Join-Path $root 'out\RecLite.exe'
$run  = Join-Path $env:TEMP ('RecLiteNoPreroll\' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
Copy-Item $exe (Join-Path $run 'RecLite.exe') -Force

function Get-WavSeconds($path) {
    $fs = [System.IO.File]::OpenRead($path)
    $br = New-Object System.IO.BinaryReader($fs)
    try {
        $null = $br.ReadBytes(12)
        $sr = 0; $ba = 0; $dataLen = 0
        while ($fs.Position -lt ($fs.Length - 8)) {
            $id = [System.Text.Encoding]::ASCII.GetString($br.ReadBytes(4))
            $len = $br.ReadUInt32()
            if ($id -eq 'fmt ') {
                $fb = $br.ReadBytes([int]$len)
                $sr = [BitConverter]::ToUInt32($fb, 4)
                $ba = [BitConverter]::ToUInt16($fb, 12)
            }
            elseif ($id -eq 'data') { $dataLen = $len; break }
            else {
                $fs.Position += $len
                if ($len % 2 -eq 1) { $fs.Position += 1 }
            }
        }
        if ($sr -eq 0 -or $ba -eq 0) { return -1 }
        return [math]::Round($dataLen / ($sr * $ba), 2)
    }
    finally { $br.Close(); $fs.Close() }
}

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

    Write-Host 'waiting 10s for preroll buffer to fill' -ForegroundColor Cyan
    Start-Sleep -Seconds 10

    Click 'RecordBtn'
    Write-Host ('recording timer: ' + (Text 'TimerText'))
    Start-Sleep -Seconds 3

    $np = WaitId 'RecordBtnNoPreroll' 5
    if (-not $np) { throw 'RecordBtnNoPreroll not found' }
    Write-Host ('no-preroll button: "' + $np.Current.Name + '" offscreen=' + $np.Current.IsOffscreen) -ForegroundColor Cyan

    Click 'RecordBtnNoPreroll'
    Start-Sleep -Seconds 7
    Write-Host ('status: ' + (Text 'StatusText')) -ForegroundColor Cyan

    $files = Get-ChildItem (Join-Path $run 'Recordings') -Filter *.wav -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending
    if (-not $files) { throw 'no wav file produced' }
    $f = $files[0].FullName
    $sec = Get-WavSeconds $f
    Write-Host ('file: ' + $f)
    Write-Host ('duration seconds: ' + $sec) -ForegroundColor Cyan

    $log = Join-Path $run 'RecLite.log'
    if (Test-Path $log) {
        $bad = Select-String -Path $log -Pattern '\[错误\]|Exception|UnhandledException' -Encoding UTF8
        if ($bad) { foreach ($b in $bad) { Write-Host ('LOG ERROR: ' + $b.Line.Trim()) -ForegroundColor Red } }
        else { Write-Host 'log: no errors' -ForegroundColor Green }
    }

    if ($sec -ge 0 -and $sec -le 6) {
        Write-Host 'RESULT: PASS - saved file has no preroll segment (~3s expected)' -ForegroundColor Green
    }
    else {
        Write-Host ('RESULT: FAIL - duration=' + $sec + 's, expected <= 6s') -ForegroundColor Red
    }
}
finally {
    Get-Process RecLite -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Host ('run dir: ' + $run) -ForegroundColor DarkGray
}
