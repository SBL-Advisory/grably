# Shared helpers for the end-to-end scripts. Dot-source it: . "$PSScriptRoot\Common.ps1"
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms

$Repo = (Resolve-Path "$PSScriptRoot\..\..").Path
$GrablyExe = "$Repo\src\Grably.App\bin\Debug\net10.0-windows10.0.26100.0\Grably.exe"
$DevCli = "$Repo\tools\Grably.DevCli\bin\Debug\net10.0-windows10.0.26100.0\grably-dev.exe"
$script:Failures = 0

foreach ($exe in $GrablyExe, $DevCli) {
    if (-not (Test-Path $exe)) { throw "Missing $exe; run 'dotnet build Grably.slnx' first." }
}

# Creates a fresh GRABLY_HOME with the given settings JSON and returns its path.
function New-GrablyHome([string]$Name, [string]$SettingsJson) {
    $script:PreviousGrablyHome = $env:GRABLY_HOME
    $runHome = Join-Path $env:TEMP "grably-e2e-$Name"
    Remove-Item -Recurse -Force $runHome -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force "$runHome\roaming" | Out-Null
    Set-Content -Encoding utf8 "$runHome\roaming\settings.json" $SettingsJson
    $env:GRABLY_HOME = $runHome
    return $runHome
}

function Start-Grably {
    $process = Start-Process $GrablyExe -ArgumentList '--background' -PassThru
    Start-Sleep -Seconds 4
    if ($process.HasExited) {
        # It handed over to a Grably that was already running: that one uses the real profile and real hotkeys.
        Restore-GrablyHome
        throw 'Grably is already running. Exit it from its tray icon, then run the end-to-end checks again.'
    }
    return $process
}

# Sends keys only if the given window is in front (a leading * matches part of the title). Fails the check otherwise,
# so a missing overlay or editor never leads to Esc, Alt+F4 or Ctrl+S landing in another app.
function Send-KeysTo([string]$Gesture, [string]$Window) {
    $output = & $DevCli send-keys $Gesture --require-foreground $Window
    if ($LASTEXITCODE -ne 0) { Write-Host "$output"; $script:Failures++; return $false }
    return $true
}

function Restore-GrablyHome {
    if ($null -eq $script:PreviousGrablyHome) { Remove-Item Env:GRABLY_HOME -ErrorAction SilentlyContinue }
    else { $env:GRABLY_HOME = $script:PreviousGrablyHome }
}

function Check([bool]$Condition, [string]$What) {
    if ($Condition) { Write-Output "PASS $What" } else { Write-Output "FAIL $What"; $script:Failures++ }
}

function Get-ImageSize([string]$Path) {
    $image = [System.Drawing.Image]::FromFile($Path)
    try { "$($image.Width)x$($image.Height)" } finally { $image.Dispose() }
}

# Waits until a folder holds more than $Count PNG files; returns the newest, or $null on timeout.
function Wait-NewPng([string]$Folder, [int]$Count, [double]$Seconds = 6) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
        $files = @(Get-ChildItem $Folder -Filter *.png -ErrorAction SilentlyContinue)
        if ($files.Count -gt $Count) {
            Start-Sleep -Milliseconds 300
            return ($files | Sort-Object LastWriteTime | Select-Object -Last 1)
        }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

function Get-PngCount([string]$Folder) { @(Get-ChildItem $Folder -Filter *.png -ErrorAction SilentlyContinue).Count }

function Test-WindowVisible([string]$Title, [double]$Seconds = 0.3, [switch]$Contains) {
    $cliArgs = @('wait-window', $Title, "$Seconds")
    if ($Contains) { $cliArgs += '--contains' }
    $output = & $DevCli @cliArgs
    return [pscustomobject]@{ Visible = ($LASTEXITCODE -eq 0); Line = "$output" }
}

function Show-LogProblems([string]$RunHome) {
    Get-Content (Get-ChildItem "$RunHome\local\logs\*.log").FullName |
        Select-String -Pattern '\[(ERR|FTL)\]' |
        ForEach-Object { Write-Output "LOG $($_.Line)" }
}

function Complete-Run([string]$RunHome, [switch]$Keep) {
    Restore-GrablyHome
    if ($Keep) { Write-Output "Kept $RunHome" } else { Remove-Item -Recurse -Force $RunHome -ErrorAction SilentlyContinue }
    if ($script:Failures -gt 0) { Write-Output "$($script:Failures) check(s) failed"; exit 1 }
    Write-Output 'All checks passed'
    exit 0
}
