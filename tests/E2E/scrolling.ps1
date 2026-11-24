# Scrolling capture and text capture end to end: a long text file in Notepad is scrolled, stitched and read back.
param([switch]$Keep)
. "$PSScriptRoot\Common.ps1"

$runHome = New-GrablyHome 'scrolling' @'
{ "hotkeys": {
    "captureRegion": "",
    "captureActiveWindow": "",
    "captureFullScreen": "",
    "scrollingCapture": "Ctrl+Alt+Shift+F20",
    "captureText": "Ctrl+Alt+Shift+F21" },
  "screenshot": { "afterCapture": "copyAndSave" } }
'@
$shots = "$runHome\Pictures\Grably"

# 400 numbered lines: far more than one screen, and every line different so the stitch can be checked by reading it.
$textFile = "$runHome\scroll-test.txt"
1..400 | ForEach-Object { "Line {0:D3} of the scrolling capture test, word{1}" -f $_, ($_ * 7) } | Set-Content -Encoding utf8 $textFile
Start-Process notepad.exe -ArgumentList "`"$textFile`""
$app = Start-Grably
try {
    $title = 'scroll-test.txt - Notepad'
    & $DevCli wait-window $title 8 | Out-Null
    $window = & $DevCli windows | Select-String ([regex]::Escape($title)) | Select-Object -First 1
    Check ($null -ne $window) 'Notepad shows the test file'
    $bounds = [regex]::Match("$window", '(-?\d+),(-?\d+) (\d+)x(\d+)')
    $center = '{0},{1}' -f ([int]$bounds.Groups[1].Value + [math]::Floor([int]$bounds.Groups[3].Value / 2)), ([int]$bounds.Groups[2].Value + [math]::Floor([int]$bounds.Groups[4].Value / 2))
    $windowHeight = [int]$bounds.Groups[4].Value

    # Scrolling capture: click the Notepad window in the overlay, then leave the mouse alone until the image is saved.
    $before = Get-PngCount $shots
    & $DevCli send-keys 'Ctrl+Alt+Shift+F20' | Out-Null
    & $DevCli wait-window 'Grably capture' 5 | Out-Null
    & $DevCli mouse click $center --require-window 'Grably capture' | Out-Null
    $file = Wait-NewPng $shots $before 90
    Check ($null -ne $file) 'the scrolling capture is saved'
    if ($file) {
        $height = [int](Get-ImageSize $file.FullName).Split('x')[1]
        Check ($height -gt $windowHeight * 3) "the image is much taller than the window ($height px vs $windowHeight px)"
        $text = (& $DevCli ocr $file.FullName) -join "`n"
        foreach ($line in 'Line 001', 'Line 200', 'Line 400') {
            Check ($text -match $line) "the stitched image contains '$line'"
        }

        # Every line once, in order: a wrong offset drops lines (a gap) or repeats them. OCR may misread a digit or two.
        $numbers = @([regex]::Matches($text, 'Line (\d{3})') | ForEach-Object { [int]$_.Groups[1].Value })
        $distinct = @($numbers | Sort-Object -Unique).Count
        $repeated = @($numbers | Group-Object | Where-Object Count -gt 1).Count
        $backwards = if ($numbers.Count -gt 1) { @(1..($numbers.Count - 1) | Where-Object { $numbers[$_] -le $numbers[$_ - 1] }).Count } else { 0 }
        Check ($distinct -ge 390) "almost every line is there ($distinct of 400)"
        Check ($repeated -le 2 -and $backwards -le 3) "lines are in order and not repeated ($repeated repeated, $backwards out of order)"
    }

    # Text capture: drag over the top of the Notepad window; the text goes to the clipboard.
    [System.Windows.Forms.Clipboard]::Clear()
    & $DevCli send-keys 'Ctrl+Alt+Shift+F21' | Out-Null
    & $DevCli wait-window 'Grably capture' 5 | Out-Null
    & $DevCli mouse click $center --require-window 'Grably capture' | Out-Null
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (-not [System.Windows.Forms.Clipboard]::ContainsText() -and $clock.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 200 }
    Check ([System.Windows.Forms.Clipboard]::GetText() -match 'Line \d{3} of the scrolling') 'capture text copies the window text'
}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    # Close only the test file's tab (Notepad may hold the user's own tabs too); it was never edited, so no prompt.
    # Best effort: this cleanup never counts as a failed check.
    & $DevCli send-keys 'Ctrl+W' --require-foreground 'scroll-test.txt - Notepad' | Out-Null
    Restore-GrablyHome
}

Show-LogProblems $runHome
Complete-Run $runHome -Keep:$Keep
