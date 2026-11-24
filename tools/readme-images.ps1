# Renders the README's screenshots of Grably into docs/images with Grably's own --ui-snapshot (Debug builds): its
# windows with sample content in a temporary folder, never anything from the screen. The Trim image needs the bundled
# ffmpeg. Run after a UI change:
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/readme-images.ps1
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path "$PSScriptRoot\..").Path
$grably = "$repo\src\Grably.App\bin\Debug\net10.0-windows10.0.26100.0\Grably.exe"
if (-not (Test-Path $grably)) { throw "Missing $grably; run 'dotnet build Grably.slnx' first." }
$images = "$repo\docs\images"
New-Item -ItemType Directory -Force $images | Out-Null

# A scratch home, so the snapshots never read or write the real settings or library.
$previousHome = $env:GRABLY_HOME
$env:GRABLY_HOME = Join-Path ([IO.Path]::GetTempPath()) "grably-readme-$([guid]::NewGuid().ToString('N'))"
try {
    foreach ($shot in @(
            @{ Window = 'LibraryShowcase'; File = 'library.png'; Theme = 'light' },
            @{ Window = 'Editor'; File = 'editor.png'; Theme = 'dark' },
            @{ Window = 'Trim'; File = 'trim.png'; Theme = 'dark' })) {
        # Grably.exe is a windowed program, so PowerShell would not wait for it with &.
        $run = Start-Process $grably -ArgumentList '--ui-snapshot', $shot.Window, '--out', "`"$images\$($shot.File)`"", '--theme', $shot.Theme -Wait -PassThru
        if ($run.ExitCode) { throw "The $($shot.Window) snapshot failed (exit code $($run.ExitCode))" }
        Write-Output "$($shot.File) from $($shot.Window) ($($shot.Theme))"
    }
}
finally {
    Remove-Item -Recurse -Force $env:GRABLY_HOME -ErrorAction SilentlyContinue
    $env:GRABLY_HOME = $previousHome
}
