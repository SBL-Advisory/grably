# End-to-end checks

PowerShell scripts that drive the real Grably app on the real desktop and check the files it produces. They complement the unit and desktop tests: they cover the whole path from a hotkey to a saved file.

Run them from the repository root after a Debug build, on an unlocked desktop you are not using at that moment. They press keys, move the mouse and click (only inside Grably's own windows: `grably-dev mouse` refuses to click unless the named Grably window is visible).

```
dotnet build Grably.slnx
powershell -NoProfile -ExecutionPolicy Bypass -STA -File tests/E2E/capture.ps1
powershell -NoProfile -ExecutionPolicy Bypass -STA -File tests/E2E/editor.ps1
powershell -NoProfile -ExecutionPolicy Bypass -STA -File tests/E2E/scrolling.ps1
powershell -NoProfile -ExecutionPolicy Bypass -STA -File tests/E2E/recording.ps1
```

Each script uses its own `GRABLY_HOME` under `%TEMP%`, so your settings, library and pictures are never touched, and deletes it at the end because the captures contain whatever was on screen. Pass `-Keep` to keep it for inspection.

| Script | Checks |
|---|---|
| `capture.ps1` | Full-screen, window and region capture sizes; the region capture is in the library; a region dragged across the first monitor boundary (if there are two monitors) is pixel exact; the clipboard matches; Esc, right-click and Alt+F4 cancel without saving and the next capture still works |
| `editor.ps1` | A region capture opens the editor; a drawn arrow is saved into the PNG with Ctrl+S; the project lists the arrow and re-renders byte-identically; the library links the project; the saved editor closes without a prompt; `Grably.exe <project>` reopens it |
| `recording.ps1` | The Record hotkey, a click on a screen, Enter in the pre-record bar, the countdown, a pause and the Stop hotkey give an MP4 of about five seconds (the pause left out) with an AAC track of the PC's sound, listed in the library, with the session cleaned up and the trim editor open on it. Needs an output device (it records whatever the PC plays, usually silence) |
| `scrolling.ps1` | A scrolling capture of a 400-line file in Notepad is several times taller than the window and, read back with OCR, contains the first, middle and last lines with none repeated; capture text copies the window's text |

Every line starting with `FAIL` is a failure; the scripts exit with code 1 if any check failed.

Do not touch the mouse or keyboard while a script runs: a real mouse movement during a synthetic drag changes the selected area and makes the size checks fail. Keys that act on a window (Esc, Alt+F4, Ctrl+S) are only sent when the expected Grably window is in front (`grably-dev send-keys --require-foreground`); if anything else is in front, the check fails instead.
