# 从 CHANGELOG.md 里截取指定版本的发布说明
#
# 用法（仓库根或任意目录执行都行）：
#   .\release-notes.ps1 -Version 0.2.2
#   .\release-notes.ps1 -Version 0.2.2 -OutFile release-notes.md
#
# 输出：该版本 "## v<版本>" 标题下的正文（到下一个 "## " 标题之前），
#       供 .github/workflows/release.yml 交给 gh release create --notes-file。

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$OutFile
)

$ErrorActionPreference = "Stop"

$scripts = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scripts
$changelog = Join-Path $root "CHANGELOG.md"

if (-not (Test-Path $changelog)) { throw "没找到 CHANGELOG.md：$changelog" }

$version = $Version -replace '^v', ''
$lines = Get-Content $changelog -Encoding UTF8
$pattern = '^##\s+\[?v?' + [regex]::Escape($version) + '\]?(?=\s|$)'

$startIdx = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match $pattern) { $startIdx = $i; break }
}
if ($startIdx -lt 0) { throw ('CHANGELOG.md 里找不到「## [{0}]」这一节' -f $version) }

$body = New-Object System.Collections.Generic.List[string]
for ($i = $startIdx + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^##\s' -or $lines[$i] -match '^\[[^\]]+\]:') { break }
    $body.Add($lines[$i])
}

while ($body.Count -gt 0 -and $body[0].Trim() -eq '') { $body.RemoveAt(0) }
while ($body.Count -gt 0 -and $body[$body.Count - 1].Trim() -eq '') { $body.RemoveAt($body.Count - 1) }
if ($body.Count -eq 0) { throw ('CHANGELOG.md 里 v{0} 这一节是空的' -f $version) }

# 发版页的固定说明头：与 CHANGELOG 分开维护 —— CHANGELOG 只写「变了什么」，
# 这段写「下载后怎么用」，两者互不侵占。
$headerLines = @(
    '单文件绿色版，下载下方的 `RecLite.exe` 双击即用；中文详细说明见附件 `RecLite-UserGuide-zh.txt`。'
    ''
    '- 系统要求：Windows 10 1903+ / Windows 11（系统自带 .NET Framework 4.8），无需安装任何运行时'
    '- 首次运行若提示「Windows 已保护你的电脑」→ 点【更多信息】→【仍要运行】（未签名，属正常提示）'
    '- 附件 `SHA256SUMS.txt` 可用于校验下载是否完整'
    ''
    '---'
    ''
)
$header = ($headerLines -join "`n")

$text = $header + "`n" + ($body -join "`n") + "`n"

if ($OutFile) {
    [System.IO.File]::WriteAllText($OutFile, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "发布说明已写入：$OutFile"
} else {
    Write-Output $text
}
