# Downloads the ffmpeg build Grably bundles into src/Grably.App/ffmpeg (gitignored), checking its SHA256.
#
# It is the LGPL shared build from FFmpeg-Builds: no GPL parts (so no libx264), DLLs instead of one large exe.
# Daily builds are deleted after 14 days; month-end builds are kept for two years, so this pins one by exact URL.
# To move to a newer build, pick a month-end release at https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases,
# take the win64-lgpl-shared asset of a release branch (n9.x), and update the three values below together with
# THIRD-PARTY-NOTICES.md.
param([switch]$Force)
$ErrorActionPreference = 'Stop'

$Name = 'ffmpeg-n9.0.2-39-g7adfbe40dc-win64-lgpl-shared-9.0'
$Url = "https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/autobuild-2026-10-31-12-59/$Name.zip"
$Sha256 = 'e907b01fa4783913151237e37df2a10fa97d99a1598a021bf59d4ae62ffed4c0'

$repo = (Resolve-Path "$PSScriptRoot\..").Path
$target = Join-Path $repo 'src\Grably.App\ffmpeg'
$stamp = Join-Path $target 'VERSION.txt'

if (-not $Force -and (Test-Path "$target\ffmpeg.exe") -and (Test-Path $stamp) -and ((Get-Content $stamp -First 1) -eq $Name)) {
    Write-Output "ffmpeg $Name is already in $target"
    exit 0
}

$work = Join-Path ([IO.Path]::GetTempPath()) "grably-ffmpeg-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $zip = Join-Path $work "$Name.zip"
    Write-Output "Downloading $Url"
    $ProgressPreference = 'SilentlyContinue' # the progress bar makes Invoke-WebRequest many times slower
    Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing

    $actual = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256) {
        throw "SHA256 mismatch for $Name.zip: expected $Sha256, got $actual. Nothing was installed."
    }

    Expand-Archive -Path $zip -DestinationPath $work
    $bin = Join-Path $work "$Name\bin"
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item "$bin\ffmpeg.exe", "$bin\ffprobe.exe", "$bin\*.dll" $target
    Copy-Item (Join-Path $work "$Name\LICENSE.txt") (Join-Path $target 'LICENSE.txt')
    Set-Content -Encoding utf8 $stamp @($Name, $Url, "sha256 $Sha256")
    Write-Output "Installed $Name into $target"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
