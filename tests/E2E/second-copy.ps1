# A different Grably.exe started while another runs (#30): it asks first. No hands the launch to the running copy;
# Yes closes the running copy (through its Exit questions) and starts this one in its place. Starting the running
# copy's own exe again, and any --background launch, hand over without asking.
# The "other copy" is this Debug build copied to a scratch folder. Dialog buttons are pressed with UI Automation, so
# the mouse and keyboard are not used. Every hotkey is off.
param([switch]$Keep)
. "$PSScriptRoot\Common.ps1"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

if (Get-Process Grably -ErrorAction SilentlyContinue) { throw 'Grably is running. Exit it from its tray icon first.' }

$runHome = New-GrablyHome 'second-copy' @'
{ "hotkeys": { "captureRegion": "", "captureActiveWindow": "", "captureFullScreen": "", "scrollingCapture": "",
               "captureText": "", "toggleRecording": "", "pauseRecording": "" } }
'@
$log = "$runHome\local\logs"
$version = ([regex]::Match((Get-Content -Raw "$Repo\Directory.Build.props"), '<Version>([^<]+)</Version>')).Groups[1].Value
$bin = Split-Path $GrablyExe
robocopy $bin "$runHome\app" /E /NFL /NDL /NJH /NJS /NP | Out-Null
$copyExe = "$runHome\app\Grably.exe"

# Both copies log to the same file, which Grably keeps open: read it with sharing allowed.
function Get-LogCount([string]$Text) {
    $count = 0
    foreach ($file in Get-ChildItem $log -Filter *.log -ErrorAction SilentlyContinue) {
        $stream = [IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite')
        try {
            $content = (New-Object IO.StreamReader $stream).ReadToEnd()
            $count += ([regex]::Matches($content, [regex]::Escape($Text), [Text.RegularExpressions.RegexOptions]::IgnoreCase)).Count
        }
        finally { $stream.Dispose() }
    }
    return $count
}

function Wait-LogCount([string]$Text, [int]$Count, [double]$Seconds) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
        if ((Get-LogCount $Text) -ge $Count) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Start-Launch([string]$Exe, [string[]]$Arguments = @()) {
    $process = if ($Arguments.Count) { Start-Process $Exe -ArgumentList $Arguments -PassThru } else { Start-Process $Exe -PassThru }
    $null = $process.Handle # keeps the exit code readable after it exits
    return $process
}

function Test-Exited($Process, [int]$Seconds) { $Process.WaitForExit($Seconds * 1000) -and $Process.ExitCode -eq 0 }

function Test-Contains([string]$Text, [string]$Part) { $Text.IndexOf($Part, [StringComparison]::OrdinalIgnoreCase) -ge 0 }

# The message box a process shows (window class #32770), or $null.
function Wait-Dialog([int]$ProcessId, [double]$Seconds = 15) {
    $condition = New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)),
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ClassNameProperty, '#32770')))
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
        $dialog = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $condition)
        if ($dialog) { return $dialog }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Get-DialogText($Dialog) {
    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text)
    ($Dialog.FindAll([Windows.Automation.TreeScope]::Descendants, $condition) | ForEach-Object { $_.Current.Name }) -join "`n"
}

# Presses a message box button by its control id (6 = Yes, 7 = No), which does not depend on the Windows language.
function Invoke-DialogButton($Dialog, [string]$Id) {
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $Dialog.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition).GetCurrentPattern(
        [Windows.Automation.InvokePattern]::Pattern).Invoke()
}

$copy = Start-Launch $copyExe '--background'
$launches = @()
try {
    Check (Wait-LogCount 'Ready' 1 30) 'the other copy starts from a scratch folder'

    # A different Grably.exe asks first; No keeps the running copy and hands the launch to it.
    $launch = Start-Launch $GrablyExe
    $launches += $launch
    $dialog = Wait-Dialog $launch.Id
    $text = if ($dialog) { Get-DialogText $dialog } else { '' }
    Check ((Test-Contains $text "Grably $version is already running from:") -and (Test-Contains $text 'grably-e2e-second-copy\app')) `
        'a different Grably.exe asks first, naming the running copy''s version and folder'
    Check ((Test-Contains $text "You started Grably $version from:") -and (Test-Contains $text $bin)) 'and its own'
    if ($dialog) { Invoke-DialogButton $dialog '7' }
    Check (Test-Exited $launch 10) 'No ends the new launch'
    Check ((Wait-LogCount 'The user chose to keep' 1 5) -and (Wait-LogCount 'Another launch forwarded 0 argument(s)' 1 5)) `
        'and hands it to the running copy, which opens its Library'
    Check (-not $copy.HasExited) 'the running copy keeps running'

    # The running copy's own exe again: no question.
    $again = Start-Launch $copyExe
    $launches += $again
    Check ((Test-Exited $again 10) -and (Wait-LogCount 'Another launch forwarded 0 argument(s)' 2 5)) `
        'starting the running copy''s own Grably.exe again hands over without asking'

    # A --background launch (as at sign-in) never asks, whatever copy it is: a dialog would keep it from exiting.
    $background = Start-Launch $GrablyExe '--background'
    $launches += $background
    Check ((Test-Exited $background 10) -and (Wait-LogCount '(background: True): HandOver' 1 5)) `
        'a --background launch of a different Grably.exe hands over without asking'

    # Yes: the running copy closes (nothing to ask about) and this one starts in its place.
    $launch = Start-Launch $GrablyExe
    $launches += $launch
    $dialog = Wait-Dialog $launch.Id
    if ($dialog) { Invoke-DialogButton $dialog '6' }
    Check ($copy.WaitForExit(30000)) 'Yes closes the running copy'
    Check ((Wait-LogCount 'has closed; starting' 1 15) -and (Wait-LogCount 'Ready' 2 30)) 'and starts this one in its place'
    Start-Sleep -Seconds 2
    Check (-not $launch.HasExited -and @(Get-Process Grably -ErrorAction SilentlyContinue).Count -eq 1) 'only one Grably runs'
}
finally {
    foreach ($process in @($copy) + $launches) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
    Restore-GrablyHome
}

Show-LogProblems $runHome
Complete-Run $runHome -Keep:$Keep
