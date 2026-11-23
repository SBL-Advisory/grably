# Screenshot capture end to end: full screen, window, region (across monitors), clipboard, and every way to cancel.
param([switch]$Keep)
. "$PSScriptRoot\Common.ps1"

$runHome = New-GrablyHome 'capture' @'
{ "hotkeys": {
    "captureRegion": "Ctrl+Alt+Shift+F18",
    "captureActiveWindow": "Ctrl+Alt+Shift+F19",
    "captureFullScreen": "Ctrl+Alt+Shift+F17" },
  "screenshot": { "afterCapture": "copyAndSave" } }
'@
$shots = "$runHome\Pictures\Grably"
$monitors = & $DevCli monitors
$monitors | ForEach-Object { Write-Output "INFO $_" }

# Drag across the seam between the first two monitors if there are two side by side, else inside the primary.
$region = @{ From = '200,100'; To = '599,399'; Size = '400x300' }
$left = $monitors | Select-String 'monitor 0  (-?\d+),(-?\d+) (\d+)x(\d+)'
if ($left -and ($monitors | Select-String 'monitor 1').Count -gt 0) {
    $m = $left.Matches[0].Groups
    $seam = [int]$m[1].Value + [int]$m[3].Value
    $region = @{ From = "$($seam - 200),100"; To = "$($seam + 199),399"; Size = '400x300' }
}

$app = Start-Grably
try {
    $before = Get-PngCount $shots
    & $DevCli send-keys 'Ctrl+Alt+Shift+F17' | Out-Null
    $file = Wait-NewPng $shots $before
    Check ($null -ne $file) "full-screen capture saved $(if ($file) { Get-ImageSize $file.FullName })"

    $before = Get-PngCount $shots
    & $DevCli send-keys 'Ctrl+Alt+Shift+F19' | Out-Null
    $file = Wait-NewPng $shots $before
    Check ($null -ne $file) "window capture saved $(if ($file) { Get-ImageSize $file.FullName })"

    $before = Get-PngCount $shots
    & $DevCli send-keys 'Ctrl+Alt+Shift+F18' | Out-Null
    & $DevCli wait-window 'Grably capture' 5 | Out-Null
    & $DevCli mouse drag $region.From $region.To --require-window 'Grably capture' | Out-Null
    $file = Wait-NewPng $shots $before
    $size = if ($file) { Get-ImageSize $file.FullName } else { 'none' }
    Check ($size -eq $region.Size) "region $($region.From) to $($region.To) is exactly $($region.Size) (got $size)"
    $clip = [System.Windows.Forms.Clipboard]::GetImage()
    Check ($clip -and "$($clip.Width)x$($clip.Height)" -eq $region.Size) 'clipboard holds the region'
    if ($clip) { $clip.Dispose() }

    foreach ($cancel in 'Esc', 'right-click', 'Alt+F4') {
        $before = Get-PngCount $shots
        & $DevCli send-keys 'Ctrl+Alt+Shift+F18' | Out-Null
        & $DevCli wait-window 'Grably capture' 5 | Out-Null
        Start-Sleep -Milliseconds 300
        if ($cancel -eq 'right-click') { & $DevCli mouse rightclick '300,300' --require-window 'Grably capture' | Out-Null }
        else { Send-KeysTo $cancel 'Grably capture' | Out-Null }
        Start-Sleep -Seconds 1
        Check (-not (Test-WindowVisible 'Grably capture').Visible) "$cancel closes the overlay"
        Check ((Get-PngCount $shots) -eq $before) "$cancel saves nothing"
    }

    $before = Get-PngCount $shots
    & $DevCli send-keys 'Ctrl+Alt+Shift+F18' | Out-Null
    & $DevCli wait-window 'Grably capture' 5 | Out-Null
    & $DevCli mouse drag '100,100' '163,147' --require-window 'Grably capture' | Out-Null
    $file = Wait-NewPng $shots $before
    Check ($file -and (Get-ImageSize $file.FullName) -eq '64x48') 'a capture after the cancels still works'
}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    Restore-GrablyHome
}

Show-LogProblems $runHome
Complete-Run $runHome -Keep:$Keep
