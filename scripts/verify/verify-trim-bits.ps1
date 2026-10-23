# 裁剪预录 · 「格式 × 预录长度」矩阵回归
# 背景：缓冲区曾固定写死 64KB，24 bit 立体声（BlockAlign=6）除不尽 → 直接抛
#       ArgumentException: Must read complete blocks。现按 BlockAlign 对齐。
# 本脚本直接加载交付 exe 里的 RecLite.Audio.AudioSaver.TrimLeadingFrames，测真实产物。
# 必须保存为 UTF-8 with BOM。

param(
    [string]$Exe
)

$ErrorActionPreference = 'Stop'

# 本脚本位于 scripts\verify\：往上两级就是仓库根
$verify     = Split-Path -Parent $MyInvocation.MyCommand.Path
$scriptsDir = Split-Path -Parent $verify
$root       = Split-Path -Parent $scriptsDir
if (-not $Exe) { $Exe = Join-Path $root 'out\RecLite.exe' }
if (-not (Test-Path $Exe)) { throw "没找到产物：$Exe（先跑一次 scripts\build.ps1）" }

# ---- C# 侧：强类型调用，避免 PowerShell 把参数包成 PSObject ----
Add-Type -TypeDefinition @'
using System;
using System.Reflection;

public static class TrimRunner
{
    public static bool Run(string exePath, string src, string dst, long framesToSkip, out string err)
    {
        err = null;
        try
        {
            var asm = Assembly.LoadFrom(exePath);
            var t = asm.GetType("RecLite.Audio.AudioSaver");
            if (t == null) { err = "找不到 RecLite.Audio.AudioSaver"; return false; }
            var m = t.GetMethod("TrimLeadingFrames", BindingFlags.Public | BindingFlags.Static);
            if (m == null) { err = "找不到 TrimLeadingFrames"; return false; }

            object[] args = new object[] { src, dst, framesToSkip, null };
            object r = m.Invoke(null, args);
            err = args[3] as string;
            return r is bool && (bool)r;
        }
        catch (TargetInvocationException tie)
        {
            var inner = tie.InnerException ?? tie;
            err = inner.GetType().Name + ": " + inner.Message;
            return false;
        }
        catch (Exception ex)
        {
            err = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }
}
'@ | Out-Null

# 载入 NAudio（供生成/校验 WAV 用）
$bundled = Join-Path $root 'src\lib\bundled'
foreach ($d in 'NAudio.Core.dll','NAudio.dll','NAudio.Wasapi.dll','NAudio.WinMM.dll') {
    $p = Join-Path $bundled $d
    if (Test-Path $p) { try { [Reflection.Assembly]::LoadFrom($p) | Out-Null } catch {} }
}

# ---- 生成 24 bit PCM WAV（任意声道）----
function New-Wav24 {
    param([string]$Path, [int]$SampleRate, [int]$Channels, [int]$Frames)
    $blockAlign = $Channels * 3
    $dataLen = $Frames * $blockAlign
    $fs = [IO.File]::Create($Path)
    $bw = New-Object IO.BinaryWriter($fs)
    try {
        $bw.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
        $bw.Write([uint32](36 + $dataLen))
        $bw.Write([Text.Encoding]::ASCII.GetBytes('WAVE'))
        $bw.Write([Text.Encoding]::ASCII.GetBytes('fmt '))
        $bw.Write([uint32]16)
        $bw.Write([uint16]1)
        $bw.Write([uint16]$Channels)
        $bw.Write([uint32]$SampleRate)
        $bw.Write([uint32]($SampleRate * $blockAlign))
        $bw.Write([uint16]$blockAlign)
        $bw.Write([uint16]24)
        $bw.Write([Text.Encoding]::ASCII.GetBytes('data'))
        $bw.Write([uint32]$dataLen)
        $buf = New-Object byte[] $dataLen
        for ($i = 0; $i -lt $buf.Length; $i++) { $buf[$i] = [byte](($i * 7) -band 0xFF) }
        $bw.Write($buf)
    } finally { $bw.Close(); $fs.Close() }
}

# ---- 生成 16 bit PCM / 32 bit float WAV ----
function New-WavByNaudio {
    param([string]$Path, [int]$SampleRate, [int]$Channels, [int]$Frames, [int]$Bits)
    if ($Bits -eq 32) {
        $fmt = [NAudio.Wave.WaveFormat]::CreateIeeeFloatWaveFormat($SampleRate, $Channels)
    } else {
        $fmt = New-Object NAudio.Wave.WaveFormat($SampleRate, $Bits, $Channels)
    }
    $w = New-Object NAudio.Wave.WaveFileWriter($Path, $fmt)
    try {
        $bytes = New-Object byte[] ($fmt.BlockAlign * $Frames)
        for ($i = 0; $i -lt $bytes.Length; $i++) { $bytes[$i] = [byte](($i * 11) -band 0xFF) }
        $w.Write($bytes, 0, $bytes.Length)
    } finally { $w.Dispose() }
}

function Get-DataFrames {
    param([string]$Path)
    $r = New-Object NAudio.Wave.WaveFileReader($Path)
    try { return [long]($r.Length / $r.WaveFormat.BlockAlign) } finally { $r.Dispose() }
}

$tmp = Join-Path $env:TEMP ('reclite_trimmat_' + (Get-Date -Format 'HHmmss'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

$rate = 48000
$totalFrames = $rate * 3          # 源文件固定 3 秒
$formats = @(
    @{ Name='16 bit · 单声道'; Bits=16; Ch=1 },
    @{ Name='16 bit · 立体声'; Bits=16; Ch=2 },
    @{ Name='24 bit · 单声道'; Bits=24; Ch=1 },
    @{ Name='24 bit · 立体声'; Bits=24; Ch=2 },
    @{ Name='32 bit 浮点 · 立体声'; Bits=32; Ch=2 }
)

# 预录长度场景（用「秒」表述，对应 UI 上用户填的值）
$skips = @(
    @{ Label='预录 0 秒（不裁）';        Frames=0;              ExpectOk=$true },
    @{ Label='预录 0.5 秒';              Frames=24000;          ExpectOk=$true },
    @{ Label='预录 2 秒';                Frames=96000;          ExpectOk=$true },
    @{ Label='预录 = 全长（按了马上停）'; Frames=$totalFrames;   ExpectOk=$false },
    @{ Label='预录 超长（远超文件）';     Frames=999999999;      ExpectOk=$false }
)

$rows = @()
foreach ($f in $formats) {
    foreach ($s in $skips) {
        $tag = ('{0}_{1}' -f $f.Bits, $s.Frames)
        $src = Join-Path $tmp "src_$tag.wav"
        $dst = Join-Path $tmp "dst_$tag.wav"

        if ($f.Bits -eq 24) { New-Wav24 -Path $src -SampleRate $rate -Channels $f.Ch -Frames $totalFrames }
        else { New-WavByNaudio -Path $src -SampleRate $rate -Channels $f.Ch -Frames $totalFrames -Bits $f.Bits }

        $err = $null
        $ok = [TrimRunner]::Run($Exe, $src, $dst, [long]$s.Frames, [ref]$err)

        $gotFrames = -1
        if ($ok -and (Test-Path $dst)) { $gotFrames = Get-DataFrames -Path $dst }
        $wantFrames = $totalFrames - $s.Frames

        $pass = $false
        $note = ''
        if ($s.ExpectOk) {
            if ($ok -and $gotFrames -eq $wantFrames) { $pass = $true }
            else { $note = "期望裁后 $wantFrames 帧，实际 $(if($ok){"$gotFrames"}else{'失败'})" }
        } else {
            if (-not $ok) { $pass = $true } else { $note = '本应因预录覆盖整段而拒绝，却成功了' }
        }
        if ($err -and -not $pass) { $note = $err }

        $rows += [pscustomobject]@{
            格式 = $f.Name
            块长 = $f.Ch * ($f.Bits / 8)
            场景 = $s.Label
            结果 = $(if ($pass) { 'PASS' } else { 'FAIL' })
            说明 = $note
        }
    }
}

Write-Host ''
foreach ($r in $rows) {
    Write-Host ("{0,-22} 块长={1}  {2,-24} {3}  {4}" -f $r.格式, $r.块长, $r.场景, $r.结果, $r.说明)
}

$fail = @($rows | Where-Object { $_.结果 -eq 'FAIL' })
Write-Host ''
Write-Host ("共 {0} 项，通过 {1}，失败 {2}" -f $rows.Count, ($rows.Count - $fail.Count), $fail.Count)
Write-Host ("临时目录: " + $tmp)
if ($fail.Count -eq 0) { Write-Host '结论: PASS'; exit 0 } else { Write-Host '结论: FAIL'; exit 1 }
