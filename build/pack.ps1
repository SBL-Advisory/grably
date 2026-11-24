# Builds the Grably installer with Velopack: a self-contained win-x64 publish with the bundled ffmpeg, packed into
# the output folder as Setup.exe, the full package, a delta from the previous release when its package is there,
# and the release feed files.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File build/pack.ps1 [-Version 0.1.10] [-Output artifacts\releases] [-AllowDevFfmpeg]
#
# The version defaults to <Version> in Directory.Build.props. The packaged ffmpeg must be Grably's minimal LGPL build
# (tools/ffmpeg/build.ps1). The developer download (tools/fetch-ffmpeg.ps1) bundles many more libraries whose
# licences THIRD-PARTY-NOTICES.md does not cover, so it is refused unless -AllowDevFfmpeg (local tries only).
param(
    [string]$Version,
    [string]$Output = 'artifacts\releases',
    [switch]$AllowDevFfmpeg
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..").Path
Push-Location $repo
try {
    if (-not $Version) {
        $match = [regex]::Match((Get-Content -Raw 'Directory.Build.props'), '<Version>([^<]+)</Version>')
        if (-not $match.Success) { throw 'No <Version> in Directory.Build.props' }
        $Version = $match.Groups[1].Value
    }

    $stamp = Get-Content 'src\Grably.App\ffmpeg\VERSION.txt' -First 1 -ErrorAction SilentlyContinue
    if (-not $stamp) {
        throw 'There is no ffmpeg in src\Grably.App\ffmpeg. Run tools\ffmpeg\build.ps1 first.'
    }
    if (-not $stamp.StartsWith('grably-ffmpeg-') -and -not $AllowDevFfmpeg) {
        throw "src\Grably.App\ffmpeg holds $stamp, not Grably's minimal LGPL build. Run tools\ffmpeg\build.ps1 (or pass -AllowDevFfmpeg for a local try that must not be shared)."
    }
    if ($stamp.StartsWith('grably-ffmpeg-')) {
        $script = (Get-FileHash -Algorithm SHA256 'tools\ffmpeg\build.sh').Hash.ToLowerInvariant()
        if ((Get-Content 'src\Grably.App\ffmpeg\BUILD-SCRIPT.sha256' -First 1 -ErrorAction SilentlyContinue) -ne $script) {
            throw 'src\Grably.App\ffmpeg was made by another version of tools\ffmpeg\build.sh. Run tools\ffmpeg\build.ps1 again.'
        }
    }

    # A fresh folder each time: a Grably started from an earlier publish keeps its files locked.
    $publish = Join-Path ([IO.Path]::GetTempPath()) "grably-publish-$([guid]::NewGuid().ToString('N'))"
    Write-Output "Publishing Grably $Version (ffmpeg: $stamp)"
    dotnet publish 'src\Grably.App\Grably.App.csproj' -c Release -r win-x64 --self-contained -o $publish "-p:Version=$Version" -nologo -v q
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }
    $required = @('Grably.exe', 'ffmpeg\ffmpeg.exe', 'ffmpeg\ffprobe.exe')
    if ($stamp.StartsWith('grably-ffmpeg-')) { $required += 'ffmpeg\licenses\ffmpeg-LGPL-2.1.txt', 'ffmpeg\BUILD.txt' }
    foreach ($file in $required) {
        if (-not (Test-Path (Join-Path $publish $file))) { throw "$file is missing from the published app" }
    }
    Copy-Item 'LICENSE', 'THIRD-PARTY-NOTICES.md' $publish

    dotnet tool restore | Out-Null
    if ($LASTEXITCODE) { throw 'dotnet tool restore failed' }
    # The package id names the install folder (%LocalAppData%\SBLAdvisory.Grably), apart from Grably's own data
    # folder (%LocalAppData%\Grably), so uninstalling never touches the user's library.
    # docs/release-notes/<version>.md, when there is one, goes into the package and onto the GitHub release.
    $notes = @()
    if (Test-Path "docs\release-notes\$Version.md") { $notes = @('--releaseNotes', "docs\release-notes\$Version.md") }
    dotnet vpk pack --packId SBLAdvisory.Grably --packVersion $Version --packDir $publish --mainExe Grably.exe `
        --packTitle Grably --packAuthors 'SBL Advisory' --icon 'assets\brand\grably.ico' --shortcuts StartMenuRoot --outputDir $Output @notes
    if ($LASTEXITCODE) { throw 'vpk pack failed' }
    Get-ChildItem $Output | Sort-Object Name | ForEach-Object { '{0,-48} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
}
finally {
    if ($publish -and (Test-Path $publish)) { Remove-Item -Recurse -Force $publish -ErrorAction SilentlyContinue }
    Pop-Location
}
