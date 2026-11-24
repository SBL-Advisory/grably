# Recording end to end: the Record hotkey, a click on a screen in the overlay, Enter in the pre-record bar, the
# countdown, about five seconds of recording with the PC's sound, the Stop hotkey, then the saved MP4, the library
# and the trim editor opening on it.
param([switch]$Keep)
. "$PSScriptRoot\Common.ps1"

$runHome = New-GrablyHome 'recording' @'
{ "hotkeys": {
    "captureRegion": "",
    "captureActiveWindow": "",
    "captureFullScreen": "",
    "toggleRecording": "Ctrl+Alt+Shift+F22",
    "pauseRecording": "Ctrl+Alt+Shift+F23" },
  "recording": { "recordSystemAudio": true, "recordMicrophone": false, "showCountdown": true, "frameRate": 30 } }
'@
$videos = "$runHome\Videos\Grably"
$ffprobe = Join-Path (Split-Path $DevCli) 'ffmpeg\ffprobe.exe'

# A point on the primary monitor away from the taskbar: clicking empty space there records that whole screen.
$primary = & $DevCli monitors | Select-String 'monitor \d+\s+(-?\d+),(-?\d+) (\d+)x(\d+).*primary'
$m = $primary.Matches[0].Groups
$point = '{0},{1}' -f ([int]$m[1].Value + 40), ([int]$m[2].Value + 40)

$app = Start-Grably
try {
    & $DevCli send-keys 'Ctrl+Alt+Shift+F22' | Out-Null
    & $DevCli wait-window 'Grably capture' 5 | Out-Null
    & $DevCli mouse click $point --require-window 'Grably capture' | Out-Null
    Check (Test-WindowVisible 'Grably: ready to record' 5).Visible 'the pre-record bar opens'
    Send-KeysTo 'Enter' 'Grably: ready to record' | Out-Null
    Check (Test-WindowVisible 'Grably recording' 8).Visible 'after the countdown the control bar shows'
    Start-Sleep -Seconds 3
    & $DevCli send-keys 'Ctrl+Alt+Shift+F23' | Out-Null # pause for two seconds: not part of the video
    Start-Sleep -Seconds 2
    & $DevCli send-keys 'Ctrl+Alt+Shift+F23' | Out-Null
    Start-Sleep -Seconds 2
    & $DevCli send-keys 'Ctrl+Alt+Shift+F22' | Out-Null

    $clock = [Diagnostics.Stopwatch]::StartNew()
    $video = $null
    while (-not $video -and $clock.Elapsed.TotalSeconds -lt 60) {
        Start-Sleep -Milliseconds 500
        $video = Get-ChildItem $videos -Filter *.mp4 -ErrorAction SilentlyContinue | Select-Object -First 1
    }

    Check ($null -ne $video) 'the recording is saved as an MP4'
    if ($video) {
        Start-Sleep -Seconds 2
        $info = & $ffprobe -v error -show_entries 'stream=codec_type,codec_name:format=duration' -of json $video.FullName | ConvertFrom-Json
        $duration = [double]$info.format.duration
        Check ($duration -gt 4 -and $duration -lt 6.5) "it lasts about five seconds without the pause ($([math]::Round($duration, 2)) s)"
        Check (@($info.streams | Where-Object codec_type -eq 'video').Count -eq 1) 'it has a video stream'
        Check (@($info.streams | Where-Object { $_.codec_type -eq 'audio' -and $_.codec_name -eq 'aac' }).Count -eq 1) 'it has the PC sound as AAC'
        $listed = & $DevCli library --expect-file $video.FullName | Select-Object -Last 1
        Check ($listed -like 'PASS*') 'the recording is in the library'
        $trim = & $DevCli wait-window "$($video.Name) - Grably trim" 10 | Select-Object -Last 1
        Check ($trim -like 'PASS*') 'the trim editor opens on it'
    }

    Check (@(Get-ChildItem "$runHome\local\sessions" -Directory -ErrorAction SilentlyContinue).Count -eq 0) 'the session folder is cleaned up'
}
finally {
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    Restore-GrablyHome
}

Show-LogProblems $runHome
Complete-Run $runHome -Keep:$Keep
