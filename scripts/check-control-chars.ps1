#requires -Version 7.0
# Control characters in the tracked documents and scripts (a release gate, and the first
# check of package-release.ps1). Written after v0.6.0: documents rewritten through Python
# string literals had "\t", "\b", "\f", octal ("\1", "\7", "\14") and hex ("\x64") escapes
# turned into control characters or other characters, and about 60 of them reached the
# public repository.
#
#   *.md, *.txt    : no [\x00-\x08\x0b\x0c\x0e-\x1f] and no tab (0x09). CR (0x0d) is allowed
#                    as part of the line end.
#   *.ps1, *.psm1  : no [\x00-\x08\x0b\x0c\x0e-\x1f] (tabs are allowed).
#
# Each hit is printed as "<file>:<line>: 0x<code>" and the exit code is 1; 0 when clean.
# The files are the ones "git ls-files" lists (generated files are not looked at), or the
# ones passed with -Path (for checking the check itself on a copy outside the repository).
#
#   pwsh -NoProfile -File scripts\check-control-chars.ps1
#   pwsh -NoProfile -File scripts\check-control-chars.ps1 -Path <copy>.md
[CmdletBinding()]
param(
    [string[]]$Path = @()
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# Hits of one file: "<display>:<line>: 0x<code>". Unknown extensions return nothing.
# The bytes are read as Latin-1 so each byte is one char: UTF-8 multi-byte sequences are all
# 0x80 or above and never match.
function Find-TcsControlChars([string]$FullPath, [string]$Display) {
    $extension = [IO.Path]::GetExtension($FullPath).ToLowerInvariant()
    switch ($extension) {
        { $_ -in '.md', '.txt' } { $pattern = '[\x00-\x09\x0b\x0c\x0e-\x1f]' }
        { $_ -in '.ps1', '.psm1' } { $pattern = '[\x00-\x08\x0b\x0c\x0e-\x1f]' }
        default { return @() }
    }
    $text = [Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes($FullPath))
    $hits = New-Object System.Collections.Generic.List[string]
    foreach ($m in [regex]::Matches($text, $pattern)) {
        $line = 1
        for ($i = $text.IndexOf("`n"); $i -ge 0 -and $i -lt $m.Index; $i = $text.IndexOf("`n", $i + 1)) { $line++ }
        $hits.Add($Display + ':' + $line + ': 0x' + ([int][char]$m.Value).ToString('X2'))
    }
    return $hits.ToArray()
}

$targets = New-Object System.Collections.Generic.List[object]
# pwsh -File passes "-Path a,b" as one string; split it here.
$Path = @($Path | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if ($Path.Count -gt 0) {
    foreach ($p in $Path) {
        $full = (Resolve-Path -LiteralPath $p).ProviderPath
        $targets.Add([pscustomobject]@{ Full = $full; Display = $p })
    }
} else {
    $listed = @(& git -C $repoRoot ls-files -- '*.md' '*.txt' '*.ps1' '*.psm1')
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed' }
    foreach ($relative in $listed) {
        $full = Join-Path $repoRoot $relative
        if (Test-Path -LiteralPath $full -PathType Leaf) {
            $targets.Add([pscustomobject]@{ Full = $full; Display = $relative })
        }
    }
}

$total = 0
foreach ($t in $targets) {
    foreach ($hit in @(Find-TcsControlChars $t.Full $t.Display)) {
        Write-Output $hit
        $total++
    }
}
Write-Output ('control-chars: files=' + $targets.Count + ' hits=' + $total)
if ($total -gt 0) { exit 1 }
exit 0
