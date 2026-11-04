# ClipIt

A standalone Windows tray app with a two-minute replay buffer, hardware video encoding, a local clip library, and an editor with separate game, Discord and microphone audio. OBS Studio does not need to be installed or running. The complete runtime and media tools are bundled.

## Install and automatic updates

Download **ClipIt-win-Setup.exe** from the [latest Windows release](https://github.com/muphy09/ClipIt/releases/latest). Quit older portable ClipIt versions from their tray menus, then run the installer once. Use the installed desktop or Start menu shortcut from then on. The installer includes the .NET runtime and required media engine; it can install the Visual C++ prerequisite if missing. Windows x64 is the only release target. This build is unsigned.

On startup ClipIt checks the public GitHub Releases feed. The footer shows **- Up to date!** after a successful check, **- Update found: Downloading** with progress while downloading, and an **- Apply Update** button when ready. Apply Update cleanly stops capture, installs the prepared update and restarts ClipIt. If you leave it downloaded, it installs on the next launch after a full **Quit ClipIt** from the tray. Closing the window leaves capture running and does not apply updates. Launching a second copy while the first is running only reopens the window.

Offline or failed checks show **Update unavailable - Retry** and leave recording running. A failed check never claims the app is up to date. Downloads are checked by Velopack before they become installable. Settings and recordings live outside the replaced current application directory. An update restarts the replay buffer; existing saved clips remain intact.

Recording starts on launch. Close the window to keep recording in the tray; double-click the tray icon to return. The tray menu offers save, pause/resume, restart, Your clips, and quit.

Defaults: **Ctrl+Shift+F10**, a two-minute rolling buffer, primary display at up to **1080p / 60 FPS / 24 Mbps**, and **Videos/ClipIt** as the clip root. The microphone starts off. Apply settings after changing a display, game, microphone, shortcut, quality, or folder. Applying starts a fresh buffer.

## Game, Discord and microphone audio

Each new recording contains four audio tracks: **ClipIt Mix** for normal playback, plus **Game**, **Discord**, and **Microphone** separately. Recording uses per-process audio capture; unrelated system audio is excluded. A disabled microphone produces a silent microphone track.

**Automatic - full-screen game** follows an eligible foreground full-screen application on the selected display and retains that application when you alt-tab. Common browsers, launchers and desktop apps are excluded. This is a window-based heuristic, not a game database. For a windowed game or a missed detection, launch the game, click **Refresh apps**, choose it under **Game & Discord audio**, and Apply. The selection matches the executable across changing window titles and relaunches.

When automatic mode detects a different game, it clears and restarts the replay buffer so the new game's folder does not receive the previous game's footage. Saving continues to capture the selected monitor, including other windows on it; choosing a game changes the audio source and folder naming, not the video capture region.

Discord is found automatically, including the standard, PTB and Canary executables. Keep its window open or minimized. Closing Discord fully into its hidden tray state can prevent window-based process discovery. Per-app audio compatibility depends on the application and Windows audio path; try a short clip before a long session. Windows 10 version 2004 or newer, or Windows 11, is required for this audio path ([OBS application audio guide](https://obsproject.com/kb/application-audio-capture-guide)).

Enable **Microphone** and select the input device. System default follows Windows' default; a named device stores its ID. Refresh devices updates the dropdown. A disconnected saved microphone stays listed instead of silently selecting another input.

## Your clips

New recordings create a subfolder for their captured application's executable name, for example `Videos/ClipIt/GameName/ClipIt <date and time>.mp4`. Before a game has been detected, automatic-mode recordings use `Desktop`. Old files stay in place.

**Your clips** shows one newest-first list across the root and all application subfolders, with thumbnails, application labels, dates, sizes and durations. There is no search field. The list updates when files are saved or changed. Import clips or drop MP4s into the library to copy them into the root folder; originals stay in place and name collisions receive a suffix.

## Editor and mixer

Click a clip to open it. Hover over the preview for play/pause, playback seeking, master preview volume and fullscreen. Click the video to play/pause. Space also toggles playback; F toggles fullscreen and Escape returns. Playback stops at the end of the selected trim range.

Drag the blue range handles to choose what to keep. There are no start/end input boxes. You can also place the playhead and use Set start here or Set end here. Focused handles support arrow keys; Shift increases the step.

The **Audio mixer** changes each source's volume or mute independently during preview. Master preview volume affects listening only. **Save trim** replaces the file with the selected range and the chosen playback mix. The four tracks remain in the file, so you can unmute or change a source again later. Ordinary players and upload services generally use the first playback mix; applications that explicitly read other tracks can access the original separate sources. Muting is not removal of the underlying audio.

Older mixed recordings are labeled **Recorded mix**; their individual sources cannot be recovered. For those clips, the game/mix slider affects the already-combined audio and the other source controls are disabled.

An animated blue bar, Saving label and Cancel button appear while a trim renders. The original remains intact until a valid replacement is ready; Cancel keeps the original. Completed trims permanently discard footage outside the selected range. Accurate trimming re-encodes video and audio. NVIDIA export is tried first, with a two-thread software fallback; rendering can add load during gaming.

Click the pencil beside the title to rename a clip, type its new name and press Enter. Escape cancels. Existing files are never overwritten by renaming. **Open in Explorer**, before **Drag clip**, opens the containing folder and selects the clip. Drag a row or the Drag clip button into another app or folder that accepts files. Apply any trim first to drag the updated file. Export Copy has been removed.

Mixer preview decodes three temporary local audio files only when opening a multitrack clip. They are cleaned up on close. Thumbnails are cached, background media jobs are bounded, and playback pauses when hiding the app or switching away. Windows media components are needed for preview.

## Capture and recovery

The default compressed buffer is roughly 370 MB, plus engine/graphics memory. A full replay is approximately 120-122 seconds because of keyframe alignment; saving early keeps the available footage. Save requests are serialized and do not stop recording. Notifications appear after the MP4 is finished.

The tray app supervises a separate recording worker and restarts it after an unexpected exit, stopped output, stalled encoder, unavailable display, or missing heartbeat. Retries back off from 2 to 60 seconds. Resume and display changes request a fresh recorder. Intentional Pause remains paused. Recovery loses the unsaved buffer; existing saved clips stay on disk. This does not recover from the entire tray app crashing, and a still-active black/static image cannot always be detected as a failure.

Capture uses Windows Graphics Capture with HDR-to-SDR conversion, hardware H.264, and 48 kHz AAC at 192 kbps per track. A supported NVIDIA, AMD or Intel hardware encoder and current drivers are required. Recording refuses CPU video encoding. A Microsoft Visual C++ x64 runtime may be required on another PC. This portable build is unsigned and does not configure automatic sign-in startup. Protected content cannot be promised to record. Long gaming sessions, exclusive fullscreen games, headset changes and performance still need testing in your setup.

Settings and rotating logs are local at `%LOCALAPPDATA%/ClipIt/`. No account or upload feature is included.

## Develop and release

The working repository is `C:\dev\ClipIt`. Open that folder in VS Code. Install .NET 10 SDK, Git, PowerShell 7, and GitHub CLI; sign in to GitHub CLI once with `gh auth login` if not already authenticated. No GitHub credentials are embedded in the installed app.

Run **Terminal > Run Task**:

| Task | Behavior |
| --- | --- |
| Test App | Builds and launches a separate development instance. Uses `.local/dev-data` and `.local/dev-clips`; never replaces or stops the installed app. Shortcut defaults to Ctrl+Alt+F8. |
| Push Hotfix | Increments patch: 0.5.0 to 0.5.1. |
| Push Minor | Increments minor and resets patch: 0.5.1 to 0.6.0. |
| Push Major | Increments major and resets minor/patch: 0.6.1 to 1.0.0. |

Push tasks run from main, validate the Windows build and media checks, create a release commit including current repository changes, tag it, atomically push main and the tag, then wait for GitHub Actions to publish the installer and update packages. Commit only intended project changes. Ordinary branch pushes do not publish updates; version tags do. If a workflow fails after pushing, rerun that existing workflow/tag instead of bumping again or force-pushing.

GitHub Actions runs only the Windows release job. It downloads pinned OBS/FFmpeg archives with checksum checks, builds a self-contained app, runs generated media checks, and uses pinned Velopack tooling to create the installer, full/delta update packages and update feed. GitHub's built-in workflow token publishes the release; no repository secret is required. Delta packages use the previous published full package when available.

For a local installer without publishing, run `./scripts/Package.ps1 -SkipPreviousRelease`. Output is in `artifacts/releases/`. The version lives in `source/ClipIt.csproj`; the title/footer read the compiled version automatically. `source/Build.ps1` also accepts -Version for isolated update tests. Build/cache outputs, developer recordings, credentials and editor state are ignored by Git.

Source is included in `source/`. Component licenses and matching upstream source archives are in `licenses/`; see THIRD-PARTY.md and LICENSE.txt. The update integration uses [Velopack](https://docs.velopack.io/integrating/overview).

`ClipIt.exe --media-checks <absolute-test-folder>` generates isolated media and verifies safe replacement, separate tracks, mixer persistence, mute isolation and restoration. `CLIPIT_DATA` isolates test settings/logs and app instances. Only isolated instances can use `CLIPIT_UPDATE_FEED` to point at a local update feed; normal installed clients always use this repository's public releases. See VALIDATION.md for performed checks and remaining limits.
