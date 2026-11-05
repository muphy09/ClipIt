# Display names - November 4, 2026

- Added monitor model names to the Display dropdown, paired with the display number. Verified the attached Samsung Odyssey monitor name in the running app. Name lookup uses the monitor interface and its EDID, with Windows' device description as fallback. Saved capture-device IDs are unchanged.

# Capture control behavior and shortcuts - November 4, 2026

- Build passed and all 44 desktop checks passed. Added coverage for Alt+B defaults, monitor-limited resolution lists (including nonstandard heights), resolution/FPS bitrate suggestions, manual bitrate preservation on replay-length changes, wheel protection on all six capture dropdowns, immediate shortcut persistence, and conflict rollback.
- Inspected the running UI: clicking Save shortcut clears its display to italic Input keybind. Entered Alt+N in a disposable profile, confirmed immediate registration/settings persistence, reopened the app and saw Alt+N restored, then pressed Alt+N and verified a completed MP4 and Clip saved feedback.
- Dropdowns and sliders share wheel protection: wheel gestures scroll their containing page and do not edit control values. The monitor maximum uses its current pixel height; encoding rounds to even dimensions. Suggested bitrate scales with resolution and FPS, while manual values remain editable.
- New profiles use Alt+B. Existing saved shortcuts are preserved. Test App no longer seeds a different default shortcut.

# Configurable replay length - November 4, 2026

- Final Test App build and launch passed. Inspected the Capture page with the compact Display dropdown, Replay length beside Frame rate, and estimated file size beneath Video bitrate.
- All 29 desktop checks passed. Added checks for the two-minute default, invalid duration rejection, all ten 30-second increments, restored selection, live size estimates, and applying/persisting both five-minute and thirty-second configurations.
- Recorded beyond a full thirty-second buffer, verified the heartbeat capped at 30 seconds, and saved a real 31.05-second MP4 (94,763,394 bytes), within the expected keyframe allowance.
- Buffer retention, memory cap, free-space checks, progress, readiness text and recovery backoff now use the configured duration. Five-minute persistence was checked; a full five-minute recording was not performed.

# Settings, startup, overlay and editor changes - November 4, 2026

- Windows x64 self-contained build passed. NuGet's vulnerability lookup was unavailable in the restricted environment (NU1900); compilation succeeded.
- All 15 existing media checks passed, including saved mixer settings, source mute isolation/restoration, safe trimming, and original-file preservation.
- All 17 new desktop checks passed with disposable data, generated recordings, and a temporary registry key. Verified startup command quoting and removal; settings persistence; development startup isolation; conditional Start minimized row; immediate overlay preference saves; enabled audio switch semantics; native overlay click-through/non-activation/taskbar flags; Windows capture exclusion; and overlay hiding.
- A hidden frontend initialized its hotkey handle, started actual hardware recording, and confirmed a successfully saved MP4 without displaying its main window. Successful save feedback uses a generated chime and overlay flash instead of the saved-clip Windows notification.
- Inspected the running Capture, Settings and editor pages. Confirmed bottom sidebar Settings navigation, removed capture descriptions, Application Selection label, Save trim beside the range, no mark/reset buttons, enabled source switches, and adjacent rename pencil.
- No real Windows startup entry was changed by validation. A full Windows sign-out/reboot, an installed-app update during minimized startup, exclusive fullscreen, and game-specific anti-cheat compatibility were not exercised. The overlay is a standard desktop window without injection or graphics hooks; visibility above exclusive fullscreen is not promised.

# ClipIt 0.5 installer/update validation - November 4, 2026

- Windows x64 self-contained release build and all 15 media integration checks passed with Velopack 1.2.161 integrated.
- Built a real Windows installer plus full update package and channel feed. Packager verified the app's update bootstrap.
- Installed an isolated 0.4.99 test package into the workspace, using its own settings and local feed. It detected 0.5.0, reported downloading progress and reached Apply Update while continuing to record.
- Launching the already-running test executable again left 0.4.99 running and its update pending; no forced update occurred.
- Inspected the actual footer, clicked Apply Update, and verified installation/restart into 0.5.0 with Up to date! visible. Test settings survived.
- Repeated with a second isolated installation: downloaded 0.5.0, exited the old frontend, relaunched it, and verified startup applied the prepared update and reopened 0.5.0 without a network fetch. The startup apply decision occurs after the single-instance gate, never in a recording worker.
- An unavailable local HTTP endpoint produced Update unavailable - Retry, with no Apply button or false Up to date status. The current version remained usable.
- Validated the four VS Code task definitions, parsed all PowerShell scripts, and checked hotfix/minor/major version arithmetic including lower-component resets.
- The Test App script launched from C:\dev\ClipIt using its isolated developer settings. The release packaging path ran from that location, including the GitHub previous-release lookup. Repeated builds archive generated output safely.
- No user's existing clip was edited or deleted for updater testing. Tests used an AppId, installation paths, settings and feeds separate from the production app.

# ClipIt 0.4 validation - November 4, 2026

- Release build succeeded with no warnings or errors after dependency verification.
- Actual libobs capture produced a playable MP4 with four named audio tracks: ClipIt Mix, Game, Discord, Microphone. A disabled microphone produced a silent microphone track.
- Two separate test applications played distinct 440 Hz and 880 Hz tones simultaneously. Recorded game audio contained the 440 Hz tone (amplitude 0.02498), with 880 Hz below 0.000001. Recorded Discord audio contained 880 Hz (0.02491), with 440 Hz below 0.000001. The mixed track contained both. This verifies the production per-process routing using controlled sources, not a live Discord call.
- The live save created the GameTone application subfolder automatically.
- Automatic mode followed a fullscreen GameTone fixture, switched to GameToneTwo, restarted its buffer and saved under GameToneTwo. The saved clip was approximately 15 seconds from a 22-second test session, confirming the previous application's buffered footage was discarded on switching.
- Fifteen media integration checks passed. These include original-file preservation on invalid range, stale file and cancellation; precise trim and atomic replacement; complete output decoding; four-track recognition; mixer metadata persistence; separate preview decoding; audio-frequency checks showing Discord removed from the mix but retained in its raw track; restoration of muted Discord in another edit; safe Windows folder names; and temporary render cleanup.
- Inspected the combined newest-first library with clips in two different application subfolders; no search field was present. Confirmed application labels, thumbnails and durations.
- Opened the editor and verified separate volume/mute controls, slider-only range selection, the pencil name field, and Open in Explorer before Drag clip. Renamed an isolated test clip and confirmed its new filename on disk.
- Clicked the video to play, scrubbed the playback slider to approximately 1.51 seconds, entered fullscreen, and returned with Escape.
- Dragged the trim end handle to approximately 2.07 seconds, muted Discord, pressed Save trim, and observed the animated blue progress bar, Saving label and Cancel button. The replacement reopened at 2.10 seconds (frame-aligned), retained four named source tracks, and persisted the Discord-muted setting.
- Open in Explorer opened the test clip's Game A folder.
- All editing used generated clips and isolated settings. Temporary test audio applications were stopped after validation. The user's older installed ClipIt instance and existing recordings were not modified.

Current limits: controlled desktop/process audio checks are not an extended gaming benchmark. Actual game and Discord-call compatibility, exclusive fullscreen, changing headsets, multi-hour recording, AMD/Intel-only hardware, and arbitrary third-party drag targets remain to be exercised in the user's setup. Legacy mixed recordings cannot acquire separate sources retroactively. The recorded monitor remains full-display video; application selection controls audio and organization.

Earlier validation below describes the behavior of earlier releases, including features since replaced by 0.4.

---

# Local validation — November 4, 2026

## ClipIt 0.3 library, editor and microphone devices

- Release build completed with no warnings or errors.
- Opened the in-app library, loaded a cached thumbnail/metadata, and opened a generated MP4 in the editor. Verified visible video frames and play/pause with selection-end stopping.
- Saved a typed 2–8 second selection from a generated 12-second clip. The original pathname then contained a valid six-second video, which reopened correctly in the player.
- Dragged the trim end handle and exported through the native Save dialog. The copy was 4.033333 seconds; its six-second source stayed unchanged.
- Initiated file drag from a clip row and dropped it back into the library. The standard file-drop payload was accepted, and the same-folder guard avoided duplicating the source. Dropping into individual third-party apps was not tested.
- Listed actual WASAPI microphone devices, selected BLUE Yeti PRO, applied settings, and verified that its stored device ID initialized the corresponding capture source. The previous 0.2 app session remained running separately.
- Ran nine isolated media integration checks: generated a two-track source; rejected an invalid range; rejected a stale source fingerprint; honored cancellation; exported a precise 3.5-second copy without altering the source; refused an existing export filename; atomically replaced a source with a three-second selection; decoded the whole result; and checked that temporary renders were cleaned up. Both audio tracks survived export and overwrite. All checks passed.
- Tests used generated clips and separate settings directories. No user's existing recordings were trimmed.
- Application-specific capture and per-track volume/mute editing are not implemented in this build. The preview volume slider changes playback only. The current recorder continues to produce a mixed system/microphone audio track.

The earlier engine and interface validation follows. Full gaming-session performance, hours-long recording, arbitrary third-party drag targets, and mid-render termination under every filesystem condition remain outside these checks.

This is a tested preview build. The following checks were performed on this PC with an NVIDIA GeForce RTX 5070 and an HDR 2560×1440 display. The recording-engine checks below include the earlier preview; the ClipIt 0.2 interface checks are listed separately.

## ClipIt 0.2 interface refresh

- Replaced the original Windows Forms settings screen with a custom WPF window, dark theme, blue accents, styled dropdowns, switches, text fields, scrollbars and buttons.
- Integrated the supplied orange logo mark in the sidebar, title bar, executable icon, taskbar icon and tray icon. The supplied original PNG is retained in the source assets.
- Renamed executable, metadata, notifications, settings/log location, single-instance coordination, clip filenames and documentation to ClipIt. Earlier preview preferences can migrate without moving or deleting recorded clips.
- Release build completed without warnings or errors after a successful dependency restore.
- Inspected the running UI at its default size and a shorter resized window. Verified the custom dropdown, scrolling to lower settings, and fixed Apply footer.
- Invalid bitrate displayed an inline error and did not interrupt recording.
- Editing the shortcut, folder and cursor switch marked settings as unsaved. Apply persisted all three preferences, disabled Apply again, and restarted capture.
- Ctrl+Alt+F9 saved an MP4 with the ClipIt filename prefix into the selected folder through the new interface.
- The final Save clip button produced a completed MP4, and killing its recording worker triggered recovery in the new WPF interface; the replacement worker started without a UI exception.
- Closing the window kept the recorder active; a shortcut sent from File Explorer saved a full replay while the app was in the tray.
- Test data and recordings remain outside the portable package. Gaming performance and long-session testing remain outstanding.

## Passed

- Release build targeting Windows x64 with its own .NET runtime; no compiler warnings on the final build.
- Direct libobs initialization and selectively loaded capture, WASAPI, FFmpeg, NVENC and Quick Sync modules. OBS Studio's frontend executable is not packaged or launched.
- Actual visible desktop footage through Windows Graphics Capture, downscaled to 1920×1080 at 60 FPS using hardware NVENC H.264 at 24 Mbps.
- System audio captured through WASAPI and encoded as 48 kHz stereo AAC. A quiet controlled tone was present in the decoded audio; peak level approximately −34.4 dBFS in that sample.
- A complete replay after the buffer filled: video 121.150 seconds, audio 121.152 seconds, file 363,715,781 bytes. The entire file decoded without errors.
- Saving a clip while recording continued; subsequent saves also completed.
- Default global Ctrl+Shift+F10 save shortcut.
- Configuring a different folder and Ctrl+Alt+F9 shortcut through the settings window; both persisted in settings.json and saved to the chosen folder.
- Triggering the configured save shortcut while File Explorer was active.
- Deliberately killing only the recording worker: the tray app stayed alive, detected the exit, waited approximately two seconds, and launched a new functioning recorder.
- Intentional Pause: recording worker exited, recording stayed off, and recovery did not undo the pause. Resume started a new worker.
- Opening the clips folder and reopening the settings window by launching the same executable again.
- Visual inspection of the settings window and decoded captured footage.

## Limits of validation

This was desktop validation, not a gaming benchmark. Exclusive-fullscreen games, long multi-hour sessions, protected content, sleep/wake, microphone quality, changing headsets/default audio devices, AMD/Intel-only encoding, and disk-full failures were not exercised end to end. Recovery checks cannot prove detection of every frozen/black frame condition.

An early DXGI capture attempt on this display produced black video; the shipped default uses the tested Windows Graphics Capture path. The test also caught and corrected OBS's requirements for helper executable locations and explicit source visibility in a frontend with no preview.

A brief desktop-idle sample while the settings window was open measured approximately 1.64% total CPU for the tray process and 0.91% for the recording worker. Memory grows while filling the buffer, so that short sample is not a steady-state bound. These numbers are machine- and workload-specific and do not predict gaming FPS impact.

Test settings, captured footage, and logs were kept outside the portable package. Launch the delivered app to begin a fresh recording session with its normal Videos/ClipIt defaults.


