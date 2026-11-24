# Installer and updates end to end, the way a user gets Grably:
#   1. packs this version and a newer test version (build/pack.ps1, so the minimal ffmpeg must be in place),
#   2. installs the first with its Setup.exe and starts it,
#   3. lets the installed Grably find the newer one in a local folder (GRABLY_UPDATE_FEED) and download it,
#   4. quits Grably and starts it again, which installs the update before it starts,
#   5. uninstalls it and checks nothing is left but the user's data (and no start-with-Windows entry).
# Settings and data go to a scratch GRABLY_HOME with every hotkey off. The installed Grably opens its Library window
# while it runs (about a minute and a half); nothing is clicked or typed.
param([switch]$Keep)
. "$PSScriptRoot\Common.ps1"

$installed = Join-Path $env:LOCALAPPDATA 'SBLAdvisory.Grably'
if (Test-Path "$installed\Update.exe") { throw "Grably is already installed in $installed. Uninstall it first." }
if (Get-Process Grably -ErrorAction SilentlyContinue) { throw 'Grably is running. Exit it from its tray icon first.' }

$version = ([regex]::Match((Get-Content -Raw "$Repo\Directory.Build.props"), '<Version>([^<]+)</Version>')).Groups[1].Value
$parts = $version.Split('.')
$newer = '{0}.{1}.{2}' -f $parts[0], $parts[1], ([int]$parts[2] + 1000) # a test version no real release will have
$work = Join-Path $env:TEMP 'grably-e2e-packages' # not grably-e2e-install: New-GrablyHome empties that one
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue

Write-Output "Packing $version and $newer"
& "$Repo\build\pack.ps1" -Version $version -Output "$work\first" | Out-Null
& "$Repo\build\pack.ps1" -Version $newer -Output "$work\feed" | Out-Null

$runHome = New-GrablyHome 'install' @'
{ "hotkeys": { "captureRegion": "", "captureActiveWindow": "", "captureFullScreen": "", "scrollingCapture": "",
               "captureText": "", "toggleRecording": "", "pauseRecording": "" } }
'@
$env:GRABLY_UPDATE_FEED = "$work\feed"
$log = "$runHome\local\logs"

# The start-with-Windows entry is per user, not per GRABLY_HOME: keep the developer's own and put it back at the end.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$ownStartup = (Get-ItemProperty $runKey -Name Grably -ErrorAction SilentlyContinue).Grably

# Grably keeps its log open for writing, so it is read with sharing allowed.
function Wait-Log([string]$Text, [double]$Seconds) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
        foreach ($file in Get-ChildItem $log -Filter *.log -ErrorAction SilentlyContinue) {
            $stream = [IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite')
            try { if ((New-Object IO.StreamReader $stream).ReadToEnd().Contains($Text)) { return $true } } finally { $stream.Dispose() }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

try {
    $setup = Get-ChildItem "$work\first" -Filter '*-Setup.exe' | Select-Object -First 1
    $size = [math]::Round($setup.Length / 1MB, 1)
    Start-Process $setup.FullName -ArgumentList '--silent' -Wait
    Check (Test-Path "$installed\current\Grably.exe") "Setup.exe ($size MB) installs Grably $version for this account"
    Check (Test-Path "$installed\current\ffmpeg\licenses\ffmpeg-LGPL-2.1.txt") 'the minimal ffmpeg and its licences are installed with it'
    $shortcut = Get-ChildItem "$env:APPDATA\Microsoft\Windows\Start Menu\Programs" -Filter 'Grably*.lnk' -ErrorAction SilentlyContinue
    Check ($null -ne $shortcut) 'it is in the Start menu'

    # A silent Setup does not start the app (a normal one does); start it the way the Start menu would.
    Start-Process "$installed\current\Grably.exe"
    Check (Wait-Log "Grably $version starting" 30) 'the installed Grably starts'

    # The automatic check runs 30 seconds after startup.
    Check (Wait-Log "Update $newer downloaded" 120) "it finds $newer in the update folder and downloads it"

    # A second launch only hands over to the running Grably: it must not install the update under it.
    $running = Get-Process Grably | Select-Object -First 1
    Start-Process "$installed\current\Grably.exe" -ArgumentList '--background' -Wait
    Start-Sleep -Seconds 2
    Check (-not $running.HasExited -and -not (Wait-Log 'Installing update' 1)) 'a second launch leaves the running Grably and the update alone'

    Get-Process Grably -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    Start-Process "$installed\current\Grably.exe" -ArgumentList '--background'
    Check (Wait-Log "Grably $newer starting" 60) "starting it again installs $newer first"

    # As if the user had turned on Start Grably when I sign in: uninstalling must remove it.
    Set-ItemProperty $runKey -Name Grably -Value "`"$installed\current\Grably.exe`" --background"
}
finally {
    Get-Process Grably -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    if (Test-Path "$installed\Update.exe") {
        Start-Process "$installed\Update.exe" -ArgumentList '--uninstall', '--silent' -Wait
    }

    Check (-not (Test-Path "$installed\current")) 'uninstalling removes the app'
    Check ($null -eq (Get-ChildItem "$env:APPDATA\Microsoft\Windows\Start Menu\Programs" -Filter 'Grably*.lnk' -ErrorAction SilentlyContinue)) 'and its Start menu shortcut'
    Check (-not (Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SBLAdvisory.Grably')) 'and its entry in Installed apps'
    Check ($null -eq (Get-ItemProperty $runKey -Name Grably -ErrorAction SilentlyContinue)) 'and its start-with-Windows entry'
    Check (Test-Path "$runHome\local\library.db") "the user's data stays"
    if ($null -ne $ownStartup) { Set-ItemProperty $runKey -Name Grably -Value $ownStartup }

    Remove-Item Env:GRABLY_UPDATE_FEED -ErrorAction SilentlyContinue
    Restore-GrablyHome
    if (-not $Keep) { Remove-Item -Recurse -Force $work, $runHome -ErrorAction SilentlyContinue }
}

if ($script:Failures) { Write-Output "FAIL install: $script:Failures check(s) failed"; exit 1 }
Write-Output 'PASS install'
