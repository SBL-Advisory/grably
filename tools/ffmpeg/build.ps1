# Builds Grably's minimal LGPL ffmpeg (tools/ffmpeg/build.sh) in Docker and puts it in src/Grably.App/ffmpeg, which
# the app, grably-dev and the tests copy next to themselves. Needs Docker Desktop with Linux containers; a build takes
# about ten minutes. The result is also kept in artifacts/ffmpeg.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/ffmpeg/build.ps1 [-Force]
#
# A build is reused only while build.sh is unchanged (it records its own SHA256 in BUILD-SCRIPT.sha256); -Force
# builds again regardless. For a quick developer setup without Docker, tools/fetch-ffmpeg.ps1 downloads a larger
# ready-made build instead (not for packaging; see build/pack.ps1).
param([switch]$Force)
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
$script = (Get-FileHash -Algorithm SHA256 "$PSScriptRoot\build.sh").Hash.ToLowerInvariant()
$target = Join-Path $repo 'src\Grably.App\ffmpeg'
$built = Join-Path $repo 'artifacts\ffmpeg'

function Test-Current([string]$Folder) {
    (Get-Content "$Folder\BUILD-SCRIPT.sha256" -First 1 -ErrorAction SilentlyContinue) -eq $script
}

if (-not $Force -and (Test-Current $target)) {
    Write-Output "ffmpeg in $target is already built by this build.sh"
    exit 0
}

if (-not $Force -and (Test-Current $built)) {
    Write-Output "Using the build in $built"
}
else {
    if (Test-Path $built) { Remove-Item -Recurse -Force $built }
    New-Item -ItemType Directory -Force $built | Out-Null
    # The image is pinned by digest (the same one as in .github/workflows), so its compilers cannot change under the
    # same script.
    $image = 'ubuntu:24.04@sha256:562111b88caa79c71391c0c4d3c2114a7d5c35915c20561fd893cb1e07929e6b'
    docker run --rm -v "${PSScriptRoot}:/build:ro" -v "${built}:/out" $image bash /build/build.sh /out
    if ($LASTEXITCODE) { throw "The ffmpeg build failed (exit code $LASTEXITCODE)" }
}

if (Test-Path $target) { Remove-Item -Recurse -Force $target }
Copy-Item -Recurse $built $target
Write-Output "ffmpeg $(Get-Content "$target\VERSION.txt" -First 1) is in $target"
