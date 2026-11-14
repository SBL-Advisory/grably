<#
.SYNOPSIS
    Bumps the Grably version in Directory.Build.props.
.PARAMETER Part
    patch (default), minor or major.
.EXAMPLE
    ./tools/bump-version.ps1 patch
#>
param(
    [ValidateSet('patch', 'minor', 'major')]
    [string]$Part = 'patch'
)

$ErrorActionPreference = 'Stop'
$props = Join-Path $PSScriptRoot '..\Directory.Build.props'
$text = [IO.File]::ReadAllText($props)
$match = [regex]::Match($text, '<Version>(\d+)\.(\d+)\.(\d+)</Version>')
if (-not $match.Success) {
    throw "No <Version>major.minor.patch</Version> found in $props"
}

$major = [int]$match.Groups[1].Value
$minor = [int]$match.Groups[2].Value
$patch = [int]$match.Groups[3].Value
switch ($Part) {
    'major' { $major++; $minor = 0; $patch = 0 }
    'minor' { $minor++; $patch = 0 }
    'patch' { $patch++ }
}

$old = "$($match.Groups[1].Value).$($match.Groups[2].Value).$($match.Groups[3].Value)"
$new = "$major.$minor.$patch"
$text = $text.Remove($match.Index, $match.Length).Insert($match.Index, "<Version>$new</Version>")
[IO.File]::WriteAllText($props, $text)
Write-Output "Version $old -> $new"
