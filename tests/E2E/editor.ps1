# Annotation editor end to end: capture opens the editor, a drawn arrow is saved, the project re-renders and reopens.
param([switch]$Keep)
. "$PSScriptRoot\Common.ps1"

$runHome = New-GrablyHome 'editor' '{ "hotkeys": { "captureRegion": "Ctrl+Alt+Shift+F18" } }'
$shots = "$runHome\Pictures\Grably"

function Get-RedCount([string]$Path) {
    $bitmap = [System.Drawing.Bitmap]::FromFile($Path)
    try {
        $n = 0
        for ($y = 0; $y -lt $bitmap.Height; $y += 2) {
            for ($x = 0; $x -lt $bitmap.Width; $x += 2) {
                $c = $bitmap.GetPixel($x, $y)
                if ($c.R -gt 200 -and $c.G -lt 70 -and $c.B -lt 80) { $n++ }
            }
        }
        $n
    } finally { $bitmap.Dispose() }
}

$app = Start-Grably
try {
    & $DevCli send-keys 'Ctrl+Alt+Shift+F18' | Out-Null
    & $DevCli wait-window 'Grably capture' 5 | Out-Null
    & $DevCli mouse drag '200,200' '799,599' --require-window 'Grably capture' | Out-Null
    $editor = Test-WindowVisible ' - Grably' 8 -Contains
    Check $editor.Visible 'the capture opens the editor'
    $capture = Get-ChildItem $shots -Filter *.png | Select-Object -First 1
    Check ($capture -and (Get-ImageSize $capture.FullName) -eq '600x400') 'the capture is the dragged 600x400'
    $redBefore = Get-RedCount $capture.FullName

    # Draw an arrow across the middle of the image (the canvas sits below the toolbars), then save.
    if ($editor.Line -notmatch 'at (-?\d+),(-?\d+) (\d+)x(\d+)') { throw 'Could not find the editor window.' }
    $x = [int]$Matches[1] + [int]([int]$Matches[3] / 2)
    $y = [int]$Matches[2] + 160 + [int](([int]$Matches[4] - 210) / 2)
    & $DevCli mouse drag "$($x - 120),$($y - 40)" "$($x + 100),$($y + 40)" --require-window '* - Grably' | Out-Null
    Start-Sleep -Milliseconds 500
    Send-KeysTo 'Ctrl+S' '* - Grably' | Out-Null
    Start-Sleep -Seconds 1
    $redAfter = Get-RedCount $capture.FullName
    Check ($redAfter -gt $redBefore + 100) "the arrow is saved into the PNG (red pixels $redBefore -> $redAfter)"

    $project = Get-ChildItem "$runHome\local\projects" -Filter *.grably -ErrorAction SilentlyContinue | Select-Object -First 1
    Check ($null -ne $project) 'saving creates a project'
    if ($project) {
        $listing = & $DevCli project $project.FullName
        Check (($listing | Select-String 'LineItem').Count -eq 1) 'the project holds the arrow'
        & $DevCli render $project.FullName --out "$runHome\rerender.png" | Out-Null
        Check ((Get-FileHash $capture.FullName).Hash -eq (Get-FileHash "$runHome\rerender.png").Hash) 'the project re-renders byte-identically'
        $listed = & $DevCli library --expect-file $capture.FullName --editable | Select-Object -Last 1
        Check ($listed -like 'PASS*') 'the library item links the saved project, so it reopens editable'
    }

    Send-KeysTo 'Alt+F4' '* - Grably' | Out-Null
    Start-Sleep -Seconds 1
    Check (-not (Test-WindowVisible ' - Grably' -Contains).Visible) 'a saved editor closes without a prompt'

    if ($project) {
        Start-Process $GrablyExe -ArgumentList "`"$($project.FullName)`"" -Wait
        Check (Test-WindowVisible ' - Grably' 8 -Contains).Visible 'Grably.exe <project> reopens it in the running app'
        Send-KeysTo 'Alt+F4' '* - Grably' | Out-Null
        Start-Sleep -Milliseconds 500
    }
}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    Restore-GrablyHome
}

Show-LogProblems $runHome
Complete-Run $runHome -Keep:$Keep
