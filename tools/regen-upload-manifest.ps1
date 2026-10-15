# 重新生成归档目录的 docs/github-upload-manifest.csv，并打印核对数字。
# Regenerates the archive manifest and prints the numbers quoted by UPLOAD-CHECKLIST.md.
#
# 用法 / Usage:  powershell -File tools/regen-upload-manifest.ps1 [-Archive <path>]
param(
    [string]$Archive = 'E:\PcApp\WorkBuddy\2026-10-14-20-18-19\LyricsBar-github-upload'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Archive)) { throw "归档目录不存在 / archive not found: $Archive" }

$manifest = Join-Path $Archive 'docs\github-upload-manifest.csv'
$notes = @{
    '.gitattributes'                          = 'line-ending normalisation (store LF)'
    '.gitignore'                              = 'excludes dist/ releases/ .preview/ bin/ obj/ *.log'
    'CHANGELOG.md'                            = 'version history'
    'LICENSE'                                 = 'MIT'
    'README.md'                               = 'Chinese README (the only README in this archive)'
    'README.zh-CN.md'                         = 'Chinese README (byte-identical to README.md)'
    'THIRD-PARTY-NOTICES.md'                  = 'dependency and source-lineage notices'
    'docs/settings-preview.png'               = 'settings window screenshot, referenced by both READMEs'
    'docs/_mdcheck.js'                        = 'maintainer tool: markdown lint used on these docs'
    'docs/github-upload-manifest.csv'         = 'this manifest'
    'tools/PureMusicProbe/PureMusicProbe.csproj' = 'pure-music / no-lyrics presentation probe'
    'tools/PureMusicProbe/Program.cs'         = 'pure-music / no-lyrics presentation probe'
    'tools/MatchProbe/MatchProbe.csproj'      = 'NetEase queue namesake / artist verification probe'
    'tools/MatchProbe/Program.cs'             = 'NetEase queue namesake / artist verification probe'
    'tools/SettingsRoundTrip/SettingsRoundTrip.csproj' = 'real SettingsWindow read/write round-trip harness'
    'tools/SettingsRoundTrip/Program.cs'      = 'real SettingsWindow read/write round-trip harness'
    'tools/regen-upload-manifest.ps1'         = 'regenerates this manifest'
}

# 不进归档清单的路径：AI 会话记忆与版本库内部文件。
# Paths excluded from the manifest: AI session memory and VCS internals.
$excluded = @(
    (Join-Path $Archive '.git'),
    (Join-Path $Archive '.workbuddy-ai')
)

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# LyricsBar - GitHub upload manifest (repository files)')
$lines.Add('# release version: 1.4.4 | repository files generated: 2026-10-15 | source root: LyricsBar')
$lines.Add('# columns: path,bytes,modified,sha256,note')

$total = 0
Get-ChildItem $Archive -Recurse -Force -File |
    Where-Object {
        $file = $_
        $file.FullName -ne $manifest -and
        -not ($excluded | Where-Object { $file.FullName -eq $_ -or $file.FullName.StartsWith($_ + '\') })
    } |
    Sort-Object FullName |
    ForEach-Object {
        $rel = $_.FullName.Substring($Archive.Length + 1).Replace('\', '/')
        $note = $notes[$rel]; if (-not $note) { $note = '' }
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
        $lines.Add(("{0},{1},{2:yyyy-MM-dd HH:mm},{3},{4}" -f $rel, $_.Length, $_.LastWriteTime, $hash, $note))
        $total += $_.Length
    }

[System.IO.File]::WriteAllLines($manifest, $lines, (New-Object System.Text.UTF8Encoding($false)))

$all = Get-ChildItem $Archive -Recurse -Force -File | Where-Object { $_.FullName -notlike "$Archive\.git\*" } | Measure-Object -Property Length -Sum
"manifest rows (excluding itself): $($lines.Count - 3)"
"manifest bytes total (excluding itself): $total"
"archive files (including the manifest): $($all.Count)"
"archive bytes (including the manifest): $($all.Sum)"
"manifest size: $((Get-Item $manifest).Length)"
