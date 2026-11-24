# Release checklist

Everything that still needs a person, or hardware the development machine does not have, before a public release. It collects the "Manual checks" of the milestone PRs (#4 to #22); the PR number is in brackets. Items that could be automated were turned into checks and are listed at the end.

Set `GRABLY_HOME` to a scratch folder first for anything run from a terminal, so the real settings and library are not touched.

## 1. On an idle desktop

These scripts click and type, so nobody may use the PC while they run. See [tests/E2E/README.md](../tests/E2E/README.md).

- [ ] `tests/E2E/capture.ps1`: capture sizes, library, clipboard, cancelling (#6, #10)
- [ ] `tests/E2E/editor.ps1`: draw, save, re-render, reopen (#8, #10)
- [ ] `tests/E2E/scrolling.ps1`: a 400-line Notepad file, capture text (#12)
- [ ] `tests/E2E/recording.ps1`: hotkey to library and the trim editor, with sound (#16, #20)
- [ ] `tests/E2E/install.ps1`: install, update from a local feed, second launch, uninstall (#22)

## 2. By hand

- [ ] **Tray:**
  - click the icon and every menu item
  - switch the taskbar between light and dark
  - start Grably twice: the Library comes to the front (#4)
  - the SBL-coloured icons: the orange idle piece and the red recording dot are easy to tell apart at 100 % and 150 % scaling, on both taskbars (#28)
  - the navy app icon stands out on a dark taskbar, in Alt+Tab and in the Start menu: navy itself is only 1.65:1 on #202020, so the white frame and orange piece carry it (#28)
- [ ] **Capture:**
  - region, window and full screen on each monitor
  - the loupe and handles
  - paste into Paint, Word and a browser
  - "Capture window" from the tray takes the app you were in
  - the Library's Capture button hides the Library (#6)
- [ ] **Editor:**
  - every tool and the property bar
  - text edited in place
  - Space-drag and Ctrl+wheel
  - the close prompt, and tray Exit with an unsaved editor
  - dragging the image into a chat app (#8)
- [ ] **Settings:**
  - record PrintScreen and Alt+PrintScreen in a shortcut box, Save, and press them
  - Start with Windows shows in Task Manager's Startup apps, then turn it off
  - switch the theme with the Library and an editor open (#10)
  - the "Built By" SBL logo switches with the theme (also when following Windows), Tab reaches it with a visible focus ring, and Enter or a click opens sbladvisory.com in the browser (#28)
  - Narrator reads the credit as "Built By Strategies Beyond Limits" (#28)
- [ ] **Scrolling capture** of a browser page, a chat and a code editor, also at 125 % scaling. This is a known limit: pages that scroll by fractional pixels may stitch with mismatches (#12).
- [ ] **Recovery:** kill Grably mid-recording, start it again, and try the Save / Delete / Later prompt (#16). Saving a killed recording is automated: `grably-dev recovery-test`.
- [ ] **Effects:**
  - record with real clicks and typing in "shortcuts only" and "all keys"
  - type in another app on a second screen while recording a window: nothing may show
  - record a window with effects while it is moved (#18)
- [ ] **Trim editor** on a real recording:
  - playback jumps the cuts without a flash
  - 0.5x and 2x
  - scrubbing, frame steps and both handles
  - MP4 and GIF export, including Cancel midway (no file left), then Play and Show in folder; the export is in the library
  - closing the window or quitting during an export asks
  - "Edit this frame" (#20)
- [ ] **Updates:** "Restart to update" from the tray, also during a recording (it must ask first) (#22)
- [ ] **Installer:** a normal (not silent) Setup.exe shows the SmartScreen prompt, then Grably opens its Library (#22)

## 3. Hardware this machine lacks

- [ ] **Microphone:**
  - the level meter moves and mute gives silence
  - unplugging or switching the microphone keeps recording
  - plugging in headphones mid-recording keeps the PC sound
  - real speaker latency: record a clap video and compare (#16)
- [ ] **Camera:**
  - bubble placement, dragging, mirror and shape
  - with Teams holding the camera
  - with camera access off in Windows privacy settings (#18)
- [ ] **NVIDIA and AMD GPUs:** `grably-dev encoders` and `record --pattern` colours (#14). The minimal ffmpeg uses NVENC API 12.2 (drivers from 550) and AMF headers 1.5.2 (#22).
- [ ] **A 5120-wide or larger monitor:** run `grably-dev bench --monitor N`. Above 4096 px, hardware H.264 fails on this Intel machine and Grably falls back to Media Foundation's software H.264 before hardware HEVC, so check that it keeps up (#14).
- [ ] **Mixed-DPI monitors:**
  - a region dragged across two monitors
  - the corner preview on each monitor (#6, #10)
- [ ] **A German or French keyboard layout with AltGr:** in "shortcuts only", typed text never shows (#18)
- [ ] **Deleting an item on a USB stick or network share:** Windows asks before deleting permanently, and No keeps the item (#10)
- [ ] **An HEVC recording:** it plays in the trim editor with the HEVC Video Extensions installed, and falls back to frame stepping without them (#20)
- [ ] **Recording while a projector or screen-mirroring app is active:** on an integrated GPU, frame rates dropped (12 to 25 fps measured in M11), with the old and new ffmpeg alike

## 4. The release

- [ ] Every PR is merged into `main`, and CI is green.
- [ ] Push the tag `v<Version>` (matching `Directory.Build.props`) from `main`.
- [ ] The release has the Setup exe, the full package, the release feed files, the portable zip and `ffmpeg-9.0.2.tar.xz`.
- [ ] Install from the release on a clean Windows account. Take every kind of capture, annotate, record with sound and camera, trim, and export MP4 and GIF.
- [ ] Publish a later version and confirm the installed copy updates itself.

## Automated in M11

| Was manual | Now |
|---|---|
| Record a window while it is resized, minimised and closed (#14) | `grably-dev window-test`. Closing the window now ends the recording; before, Windows' event did not always arrive. |
| Kill Grably mid-recording (#16) | `grably-dev recovery-test`. A killed recording now keeps all but about the last second; before, the first five seconds were still in ffmpeg's buffers. |
| Capture text on dark-theme and small UI text (#12) | `grably-dev ocr --selftest` in five looks: dark theme, 11 px, dim text, text on a button. At 12 px, white text on a blue button can lose punctuation (a Windows OCR limit). |
| Copy text in the editor on a cropped screenshot (#12) | `EditorTextSourceTests` |
| CI passes on GitHub (#4) | Green on every PR since #22 |
