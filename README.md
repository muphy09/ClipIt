# ClipIt

A standalone Windows tray app with a configurable replay buffer, hardware video encoding, a local clip library, and an editor with separate game, Discord and microphone audio. OBS Studio does not need to be installed or running. The complete runtime and media tools are bundled.

## Install and automatic updates

Download **ClipIt-win-Setup.exe** from the [latest Windows release](https://github.com/muphy09/ClipIt/releases/latest). Quit older portable ClipIt versions from their tray menus, then run the installer once. Use the installed desktop or Start menu shortcut from then on. The installer includes the .NET runtime and required media engine; it can install the Visual C++ prerequisite if missing. Windows x64 is the only release target. This build is unsigned.

On startup ClipIt checks the public GitHub Releases feed. The footer shows **- Up to date!** after a successful check, **- Update found: Downloading** with progress while downloading, and an **- Apply Update** button when ready. Apply Update cleanly stops capture, installs the prepared update and restarts ClipIt. If you leave it downloaded, it installs on the next launch after a full **Quit ClipIt** from the tray. Closing the window leaves capture running and does not apply updates. Launching a second copy while the first is running only reopens the window.

Offline or failed checks show **Update unavailable - Retry** and leave recording running. A failed check never claims the app is up to date. Downloads are checked by Velopack before they become installable. Settings and recordings live outside the replaced current application directory. An update restarts the replay buffer; existing saved clips remain intact.

Recording starts on launch. Close the window to keep recording in the tray; double-click the tray icon to return. The tray menu offers save, pause/resume, restart, Your Clips, and quit.

Defaults for new profiles: **Alt+B**, a two-minute rolling buffer, primary display at up to **1080p / 60 FPS / 24 Mbps**, and **Videos/ClipIt** as the clip root. Existing saved shortcuts are preserved. The microphone starts off. Apply settings after changing a display, game, microphone, quality, or folder. Applying starts a fresh buffer.

Click **Capture shortcut** to show the italic **Input keybind** prompt, then press a combination containing Ctrl or Alt. Valid shortcuts register and save immediately, without Apply settings or a recorder restart. If another app uses the combination, ClipIt keeps the previous shortcut. Leaving an empty prompt restores the current shortcut. Scrolling over closed dropdowns or sliders scrolls the page without changing their values; an open dropdown scrolls its own list.

**Your Clips shortcut** defaults to **Alt+Shift+B**. Change it in **Save Location** just like the capture shortcut. It opens Your Clips on Recent, newest first, restores a minimized window, and brings ClipIt back from the tray. Both shortcuts save immediately; conflicting combinations keep the previous binding. ClipIt must be running (including in the tray) for global shortcuts to work.

The status card says **Replay buffer is building** until the selected length is ready, then **Recording is active. Last x minutes/seconds ready for capture.** Paused recording says **Recording has been paused.** The sidebar displays **Replay Length** using the applied setting.

The **Apply settings** bar appears only when capture settings differ from the saved values and says **Changes have been made**. Reverting edits or applying them hides it. Shortcut changes save immediately without showing the bar. Capture warnings appear below their related section; shortcut warnings appear below **Save Location** and clear when shortcut entry is canceled, corrected, or left behind.

Choose **Replay length** beside **Frame rate**: 30 seconds to 5 minutes in 30-second steps. Click **Apply settings** to start a fresh buffer at the selected length. Resolution choices stop at the selected monitor's current height, including nonstandard heights such as 900p. Changing resolution or frame rate suggests a new bitrate, which also updates the estimated full-clip file size beneath **Video bitrate**. You can override the bitrate directly; the next resolution or frame-rate change suggests a new value again. Changing replay length preserves your manual bitrate. The size estimate includes all four audio tracks and approximate container overhead. Actual size varies with content and keyframe timing.

## Settings and recording feedback

**Replay storage** defaults to disk to reduce ClipIt's RAM usage. Temporary replay segments live in `.clipit-buffer` under the selected save folder, so the buffer uses that folder's drive. Only the recent replay is retained; saving joins its encoded video and four audio tracks into the usual MP4 without re-encoding. The hardware encoder, resolution, FPS, bitrate, library, editor, hotkey, overlay and confirmation chime work in both modes. Saving waits for the next keyframe and finalizes the clip while recording continues.

Turn on **Save replay in RAM** in Settings to use an in-memory replay instead. The switch saves immediately and starts a fresh buffer, preserving existing saved clips; changing it is blocked while a clip is saving. If Windows identifies the buffer drive as a traditional HDD, ClipIt warns that continuous writes can compete with games and recommends RAM buffering or an SSD save folder. Drives that do not report their type show an explicit unknown-drive notice rather than being assumed SSDs.

Disk mode writes continuously and requires free space for both the rolling buffer and a saved clip. Completed segments are inspected once in the background so saving can reuse their timing information instead of scanning the entire buffer again. Old segments are removed automatically; generated sessions are cleaned on shutdown and abandoned sessions on the next recorder start. Active sessions and unrelated files are preserved. A full or unavailable drive reports a capture error and enters the normal recovery path. It does not silently change recording quality or switch storage modes.

The **Settings** tab at the bottom of the sidebar saves changes immediately. **Start with Windows** registers ClipIt for the current user's Windows sign-in; it is off by default. When enabled, **Start minimized** appears below it. With that option on, Windows startup begins capture in the system tray without showing the main window. Opening ClipIt manually still opens its window. Installed builds use the stable launcher so automatic updates preserve the startup path. Windows startup registration is disabled in isolated development/test instances to keep them separate from the installed app.

**Instant Replay overlay**, at the bottom of the **Capture** tab, shows a green check while replay is active and a red X while it is inactive, including pause and recovery. Use the corner picker next to the toggle to position it on the selected capture display. The **Recording** tab has a separate corner picker for its indicator, which follows the selected display or window. Both positions default to bottom right and are saved independently. The replay badge is enabled by default, accepts no mouse input, does not take keyboard focus, and is excluded from Windows capture on supported Windows versions. Desktop and borderless applications are supported. Exclusive fullscreen and other overlays may cover it.

The replay overlay is an icon-only 22 × 22 badge with a 12-point check or X. ClipIt plays a short chime immediately when it accepts a capture request, while the clip processes in the background. After the clip finishes saving successfully, the enabled badge flashes green. Replay buffer messages, recovery warnings, and save errors appear in ClipIt. Unavailable or already-busy save requests do not play the chime.

## Game, Discord and microphone audio

Each new recording contains four audio tracks: **ClipIt Mix** for normal playback, plus **Game**, **Discord**, and **Microphone** separately. Recording uses per-process audio capture; unrelated system audio is excluded. A disabled microphone produces a silent microphone track.

**Automatic - full-screen game** follows an eligible foreground full-screen application on the selected display and retains that application when you alt-tab. Common browsers, launchers and desktop apps are excluded. This is a window-based heuristic, not a game database. For a windowed game or a missed detection, launch the game, click **Refresh apps**, choose it under **Application Selection**, and Apply. The selection matches the executable across changing window titles and relaunches.

When automatic mode detects a different game, it clears and restarts the replay buffer so the new game's folder does not receive the previous game's footage. Saving continues to capture the selected monitor, including other windows on it; choosing a game changes the audio source and folder naming, not the video capture region.

Discord is found automatically, including the standard, PTB and Canary executables. Keep its window open or minimized. Closing Discord fully into its hidden tray state can prevent window-based process discovery. Per-app audio compatibility depends on the application and Windows audio path; try a short clip before a long session. Windows 10 version 2004 or newer, or Windows 11, is required for this audio path ([OBS application audio guide](https://obsproject.com/kb/application-audio-capture-guide)).

Enable **Microphone** and select the input device. System default follows Windows' default. ClipIt remembers a named device, even when it is disconnected. Use **Test Mic** to open a live meter and adjust **Boost Mic** while speaking. On Capture, click **Apply settings** to use that boost in new replays. Recording follows the Capture boost until you set a separate boost in the Recording tab.

## Your Clips

New recordings create a subfolder for their captured application's executable name, for example `Videos/ClipIt/GameName/GameName <date and time>-<unique suffix>.mp4`. Before a game has been detected, automatic-mode recordings use `Desktop` for both the folder and filename. Old files stay in place.

**Your Clips** opens on **Recent**, a newest-first list across the root and all application subfolders, with thumbnails, application labels, dates, sizes and durations. **By Game** shows alphabetically sorted game folders, including Desktop, with the clip count and combined size for each. Open a folder to see only its clips, newest first; **All games** returns to the folders. Imported clips at the library root appear under **Unsorted** because their captured game is unknown. Both views update when clips change.

Right-click a clip for **Copy** (paste the file into another folder or app) and **Delete** (move it to the Recycle Bin). Import clips or drop external MP4s into the library to copy them into the root folder; originals stay in place and name collisions receive a suffix. Clips already anywhere in the library are ignored when dropped or imported, so dragging a clip back into Your Clips does not duplicate it.

## Editor and mixer

Click a clip to open it. Hover over the preview for play/pause, playback seeking, master preview volume and fullscreen. Click the video to play/pause. Space also toggles playback; F toggles fullscreen and Escape returns. Playback stops at the end of the selected trim range.

Drag the blue range handles to choose what to keep, then use **Save trim** beside the selected range. Focused handles support arrow keys; Shift increases the step.

The **Audio mixer** changes each source's volume independently during preview. Source switches start on for unmuted clips; turning a switch off mutes that source. Reopening a previously edited clip preserves its saved mix. Master preview volume affects listening only. **Save trim** replaces the file with the selected range and the chosen playback mix. The four tracks remain in the file, so you can unmute or change a source again later. Ordinary players and upload services generally use the first playback mix; applications that explicitly read other tracks can access the original separate sources. Muting is not removal of the underlying audio.

Older mixed recordings are labeled **Recorded mix**; their individual sources cannot be recovered. For those clips, the game/mix slider affects the already-combined audio and the other source controls are disabled.

An animated blue bar, Saving label and Cancel button appear while a trim renders. The original remains intact until a valid replacement is ready; Cancel keeps the original. Completed trims permanently discard footage outside the selected range. Accurate trimming re-encodes video and audio. NVIDIA export is tried first, with a two-thread software fallback; rendering can add load during gaming.

Click the pencil beside the title to rename a clip, type its new name and press Enter. Escape cancels. Existing files are never overwritten by renaming. **Open in Explorer**, before **Drag clip**, opens the containing folder and selects the clip. Drag a row or the Drag clip button into another app or folder that accepts files. Apply any trim first to drag the updated file. Export Copy has been removed.

Mixer preview decodes three temporary local audio files only when opening a multitrack clip. They are cleaned up on close. Thumbnails are cached, background media jobs are bounded, and playback pauses when hiding the app or switching away. Windows media components are needed for preview.

## Capture and recovery

At the default quality, two minutes of compressed replay is roughly 370 MB. Disk mode stores it on the save drive; RAM mode holds it in memory, in addition to engine/graphics memory. Longer replay lengths increase the buffer's storage requirement. A full replay can include a few extra seconds for keyframe alignment; saving early keeps the available footage. Save requests are serialized and do not stop recording. The chime immediately acknowledges an accepted request; the overlay flashes after the MP4 is finished.

The tray app supervises a separate recording worker and restarts it after an unexpected exit, stopped output, stalled encoder, unavailable display, or missing heartbeat. Retries back off from 2 to 60 seconds. Resume and display changes request a fresh recorder. Intentional Pause remains paused. Recovery loses the unsaved buffer; existing saved clips stay on disk. This does not recover from the entire tray app crashing, and a still-active black/static image cannot always be detected as a failure.

Capture uses Windows Graphics Capture with HDR-to-SDR conversion, hardware H.264, and 48 kHz AAC at 192 kbps per track. A supported NVIDIA, AMD or Intel hardware encoder and current drivers are required. Recording refuses CPU video encoding. A Microsoft Visual C++ x64 runtime may be required on another PC. This build is unsigned; automatic sign-in startup is opt-in under Settings. Protected content cannot be promised to record. Long gaming sessions, exclusive fullscreen games, headset changes and performance still need testing in your setup.

Settings and rotating logs are local at `%LOCALAPPDATA%/ClipIt/`. No account or upload feature is included.

## Develop and release

Settings includes a **Patch Notes** button that opens a themed, scrollable window. Edit the root `patchnotes` file before building: use `## [v0.5.7]` for version headings, `### Bugfixes` (or other section names) for sections, and `- example` for bullet points. Plain paragraphs and wrapped bullet text are supported. The file is copied alongside the app in development builds and release packages; put the newest version first.

The working repository is `C:\dev\ClipIt`. Open that folder in VS Code. Install .NET 10 SDK, Git, and GitHub CLI; sign in to GitHub CLI once with `gh auth login` if not already authenticated. VS Code tasks use the full path to Windows' built-in PowerShell, so PowerShell 7 is not required. No GitHub credentials are embedded in the installed app.

Run **Terminal > Run Task**:

| Task | Behavior |
| --- | --- |
| Test App | Builds and launches a separate development instance. Uses `.local/dev-data` and `.local/dev-clips`; never replaces or stops the installed app. New profiles default to Alt+B; existing saved shortcuts are preserved. |
| Push Hotfix | Increments patch: 0.5.0 to 0.5.1. |
| Push Minor | Increments minor and resets patch: 0.5.1 to 0.6.0. |
| Push Major | Increments major and resets minor/patch: 0.6.1 to 1.0.0. |

Push tasks run from main, validate the Windows build and media checks, create a release commit including current repository changes, tag it, atomically push main and the tag, then wait for GitHub Actions to publish the installer and update packages. Commit only intended project changes. Ordinary branch pushes do not publish updates; version tags do. If a workflow fails after pushing, rerun that existing workflow/tag instead of bumping again or force-pushing.

GitHub Actions runs only the Windows release job. It downloads pinned OBS/FFmpeg archives with checksum checks, builds a self-contained app, runs generated media checks, and uses pinned Velopack tooling to create the installer, full/delta update packages and update feed. GitHub's built-in workflow token publishes the release; no repository secret is required. Delta packages use the previous published full package when available.

For a local installer without publishing, run `./scripts/Package.ps1 -SkipPreviousRelease`. Output is in `artifacts/releases/`. The version lives in `source/ClipIt.csproj`; the title/footer read the compiled version automatically. `source/Build.ps1` also accepts -Version for isolated update tests. Build/cache outputs, developer recordings, credentials and editor state are ignored by Git.

Source is included in `source/`. Component licenses and matching upstream source archives are in `licenses/`; see THIRD-PARTY.md and LICENSE.txt. The update integration uses [Velopack](https://docs.velopack.io/integrating/overview).

`ClipIt.exe --media-checks <absolute-test-folder>` generates isolated media and verifies safe replacement, separate tracks, mixer persistence, mute isolation and restoration. `CLIPIT_DATA` isolates test settings/logs and app instances. Only isolated instances can use `CLIPIT_UPDATE_FEED` to point at a local update feed; normal installed clients always use this repository's public releases. See VALIDATION.md for performed checks and remaining limits.
