#Requires -Version 5.1
<#
.SYNOPSIS
    Checks that every relative link in a tracked Markdown file points at a file that is also
    tracked.

.DESCRIPTION
    Some paths are kept locally but excluded from git. A published document linking to one is
    a dead end for anyone who cloned the repository: the link resolves on the author's machine
    and nowhere else, so a manual read-through will not catch it.

    Two failures are reported:

      BROKEN       the target does not exist at all
      UNPUBLISHED  the target exists locally but is not tracked by git

    External links (http, https, mailto) and same-page anchors are skipped. Link targets are
    resolved relative to the file that contains them.

.EXAMPLE
    pwsh scripts/check-doc-links.ps1

.OUTPUTS
    Exit code 0 when every link resolves to a tracked file, 1 otherwise.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root = git rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0 -or -not $root) {
    Write-Error 'Not inside a git repository.'
    exit 1
}
$root = [System.IO.Path]::GetFullPath($root)

# -co --exclude-standard is what git would track: committed files plus untracked ones that
# are not ignored. Using the index alone would miss files staged for this branch's commit.
$tracked = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($line in (git -C $root ls-files -co --exclude-standard)) {
    if ($line) { [void]$tracked.Add($line.Replace('\', '/')) }
}

$markdown = @($tracked | Where-Object { $_ -like '*.md' } | Sort-Object)
if ($markdown.Count -eq 0) {
    Write-Host 'No tracked Markdown files.'
    exit 0
}

# Captures [text](target) and ![alt](target), including an optional "title" after the target.
$linkPattern = '!?\[[^\]]*\]\(\s*<?([^)>\s]+)>?[^)]*\)'
$problems = New-Object System.Collections.Generic.List[string]
$linkCount = 0

foreach ($file in $markdown) {
    $directory = Split-Path -Parent $file
    $content = Get-Content -LiteralPath (Join-Path $root $file) -Raw -Encoding UTF8

    foreach ($match in [regex]::Matches($content, $linkPattern)) {
        $target = $match.Groups[1].Value.Split('#')[0].Trim()

        # A bare "#anchor" is a same-page link; nothing to resolve.
        if (-not $target) { continue }
        if ($target -match '^(https?:|mailto:|tel:)') { continue }

        $linkCount++
        $target = [uri]::UnescapeDataString($target)

        $relative = if ($directory) { "$directory/$target" } else { $target }
        $absolute = [System.IO.Path]::GetFullPath((Join-Path $root $relative))
        $normalized = $absolute.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')

        if (-not (Test-Path -LiteralPath $absolute)) {
            $problems.Add("BROKEN       $file -> $target")
            continue
        }

        # A directory counts as published when git tracks anything beneath it.
        $isDirectory = (Get-Item -LiteralPath $absolute) -is [System.IO.DirectoryInfo]
        $published = $tracked.Contains($normalized)
        if (-not $published -and $isDirectory) {
            $prefix = "$normalized/"
            $published = @($tracked | Where-Object { $_.StartsWith($prefix, 'OrdinalIgnoreCase') }).Count -gt 0
        }

        if (-not $published) {
            $problems.Add("UNPUBLISHED  $file -> $target  (exists locally, not tracked by git)")
        }
    }
}

Write-Host "Checked $linkCount relative links across $($markdown.Count) tracked Markdown files."

if ($problems.Count -gt 0) {
    Write-Host ''
    foreach ($problem in $problems) { Write-Host "  $problem" }
    Write-Host ''
    # Write-Host rather than Write-Error: the exit code carries the failure, and a stack
    # trace pointing into this script tells a reader nothing about the broken link.
    Write-Host "FAILED - $($problems.Count) link(s) do not resolve to a tracked file."
    exit 1
}

Write-Host 'OK - every link resolves to a tracked file.'
exit 0
