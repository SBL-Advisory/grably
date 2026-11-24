# Grably

Windows 11 app for screenshots, annotation and screen recording. C# on .NET 10 with WPF. The v1 plan, the milestone status and decisions made during implementation are in [docs/PLAN.md](docs/PLAN.md); read it before starting work and tick the milestone checklist when a milestone's PR opens.

## Commands

```
dotnet build Grably.slnx -warnaserror                                              # build everything
dotnet test --solution Grably.slnx --filter-not-trait "Category=Desktop"           # unit tests (what CI runs)
dotnet test --solution Grably.slnx --filter-trait "Category=Desktop"               # tests that need the real desktop
src/Grably.App/bin/Debug/net10.0-windows10.0.26100.0/Grably.exe                    # run the app (starts in the tray)
Grably.exe --background                                                            # start without opening the Library
Grably.exe --ui-snapshot Library --out library.png [--theme light|dark]            # Debug builds: render a window to PNG (also LibraryShowcase, LibraryEmpty, Settings, SettingsConflict, Editor, Trim, Overlay*)
tools/Grably.DevCli/bin/Debug/net10.0-windows10.0.26100.0/grably-dev.exe help      # dev harness commands
powershell -NoProfile -ExecutionPolicy Bypass -STA -File tests/E2E/capture.ps1     # end-to-end checks on the real desktop
powershell -NoProfile -ExecutionPolicy Bypass -STA -File tests/E2E/editor.ps1      #   (see tests/E2E/README.md)
powershell -NoProfile -ExecutionPolicy Bypass -STA -File tests/E2E/scrolling.ps1   #   scrolling capture and capture text
powershell -NoProfile -ExecutionPolicy Bypass -STA -File tests/E2E/recording.ps1   #   a recording from hotkey to library
powershell -NoProfile -ExecutionPolicy Bypass -STA -File tests/E2E/install.ps1     #   install, update from a local feed, uninstall
powershell -NoProfile -ExecutionPolicy Bypass -STA -File tests/E2E/second-copy.ps1 #   a different Grably.exe while one runs: ask, hand over, replace
python tools/brand/build_brand.py                                                  # regenerate assets/brand
./tools/bump-version.ps1 patch                                                     # bump <Version> in Directory.Build.props
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ffmpeg/build.ps1         # build the minimal LGPL ffmpeg that ships (Docker) into src/Grably.App/ffmpeg
powershell -NoProfile -ExecutionPolicy Bypass -File tools/fetch-ffmpeg.ps1         #   or download a larger developer build (never packaged)
powershell -NoProfile -ExecutionPolicy Bypass -File build/pack.ps1                 # Velopack installer and update packages in artifacts/releases
grably-dev record --pattern --seconds 5 | record --monitor 0 | bench --monitor 1    # recording engine checks (ffprobe, colours, drops)
grably-dev synctest --seconds 10 --pause | exclusion-test                          # audio/video sync (no sound played); chrome stays out of captures
grably-dev effects-test [--keep]                                                   # highlight, click ring, caption and camera drawn into a recording
grably-dev export --sample --keep 1-3,5-8.5 [--format gif] [--height 720]          # trim export: length, sound, size and frame-exact cuts (or a video file)
grably-dev window-test | recovery-test | ocr --selftest                            # a window resized, minimised and closed; a killed recording saved; OCR in five looks
powershell -NoProfile -ExecutionPolicy Bypass -File tools/readme-images.ps1         # re-render the README screenshots (docs/images) with --ui-snapshot
```

Set `GRABLY_HOME` to a scratch folder before running the app, DevCli or end-to-end checks so settings, data and logs go there instead of the real profile. Logs: `<data>/logs/grably-YYYYMMDD.log`.

What still needs a person or other hardware before a release is in [docs/RELEASE-CHECKLIST.md](docs/RELEASE-CHECKLIST.md); add to it whenever a PR lists manual checks.

## Layout

- `src/Grably.Core` - net10.0, no UI and no Win32: settings, hotkey gestures, paths, models and pure logic. Unit-test everything here.
- `src/Grably.Platform` - Windows integration (Win32 via CsWin32, WinRT, D3D). No WPF. Add Win32 APIs to `NativeMethods.txt`.
- `src/Grably.App` - the WPF exe (`Grably.exe`): `Program.Main` (single instance, DI host, Serilog), tray, windows, view models. `Shell/AppCommands` is the one place user actions are routed.
- `tools/Grably.DevCli` - `grably-dev`, drives features from the terminal and prints PASS/FAIL lines.
- `tests/Grably.Tests` - xUnit v3. Tag tests that need an interactive desktop with `[Trait("Category", "Desktop")]`.
- `assets/brand` - generated brand assets (see its README); the app links them as resources, never copies them.

## Rules

- Never use the em dash character anywhere (code, UI strings, docs, commits, PRs). Use " - ".
- Every change follows the change-workflow skill: issue, branch `<issue#>-<slug>`, code review, `/bump-version patch`, commit, PR, never merge. The v1 milestones are stacked PRs (each branch starts from the previous milestone branch).
- All capture geometry is in physical pixels of the virtual desktop; convert to WPF DIPs only at the view boundary.
- Library APIs move fast (WPF-UI 4.x, NAudio 3.x, Velopack, Vortice): check the package XML docs in `~/.nuget/packages` before using an API from memory.
- The agent cannot see the screen: make features verifiable through DevCli commands, `--ui-snapshot` images, log lines, ffprobe output or extracted frames. List anything that still needs a human under "Manual checks" in the PR.
- Never log keystrokes or capture content.
- Recording uses only the bundled LGPL ffmpeg (`src/Grably.App/ffmpeg`), never the one on PATH, and never libx264. The shipped build has only the codecs, formats and filters listed in `tools/ffmpeg/build.sh`: anything new Grably asks of ffmpeg goes on those lists, and the checks run against that build.
