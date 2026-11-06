# Disk replay save latency — November 6, 2026

The current disk save implementation adds substantial avoidable work after the save request. The measured export cost alone explains most of the reported 5–10 second delay. This is not evidence that the user's drive is defective or too slow.

## Evidence

- `source/Recorder.cs:298` requests a native file split. The pinned OBS 33.0.1 source (`plugins/obs-ffmpeg/obs-ffmpeg-mux.c:695–700`) waits for a video keyframe before splitting. ClipIt sets a two-second keyframe interval. Under normal operation this adds approximately 0–2 seconds, with additional encoder/muxer scheduling possible.
- `source/Recorder.cs:389` runs `Recorder.Tick()` once per second. Disk segment events and completed exports are processed from that tick, potentially adding almost one second at each stage. The worker itself wakes every 50 ms.
- `source/DiskReplayBuffer.cs:95–105` serially starts `ffprobe` for every retained segment at save time. Each invocation enumerates video packets to calculate the segment's precise video span. A full two-minute replay has about 60 segments; five minutes has about 150. These results are discarded after every export and recomputed on subsequent saves.
- `source/DiskReplayBuffer.cs:119–131` then joins the segments using stream copy, writes an MP4 with faststart, verifies its audio tracks, and publishes it by moving the temporary file. There is no video re-encoding in this path.
- `source/ClipWindow.xaml.cs:225–233` plays the chime when the saved message arrives. The sound correctly follows successful publication; delaying the sound is a consequence of the save work.

## Measurements

Used a previously generated, isolated 1080p60 hardware-capture test clip at the default 24 Mbps setting, containing four audio tracks. Split 120 seconds into 60 independent MP4 segments without re-encoding. No user recordings, installed settings, or active recorder were modified.

A temporary .NET harness compiled the current production `DiskReplayBuffer`, `MediaTools`, `Settings`, and `BufferDrive` sources directly. It invoked the actual production segment-duration method and actual `DiskReplayBuffer.Export` with the bundled media tools.

| Trial | Inspect all 60 segments separately | Actual complete production export |
| --- | ---: | ---: |
| 1 | 3.624 s | 5.329 s |
| 2 | 3.608 s | 5.306 s |
| 3 | 3.529 s | 5.123 s |

The inspection measurement and complete export measurement are separate operations. The complete export includes its own inspection; the two columns must not be added.

An independent phase benchmark using the same packet-duration calculation and stream-copy/faststart settings measured assembly at 1.418–1.560 seconds and the final metadata probe at 0.057–0.069 seconds. Its inspection phase took 3.280–3.884 seconds. Process caches, filesystem caching, background load, and the benchmark wrapper affect timings.

All three production exports retained four named audio tracks and produced a 366,546,649-byte, 120.083-second clip. The first export decoded completely across all five streams with FFmpeg's `-xerror` enabled and exit code zero.

These are export-component measurements, not a newly instrumented hotkey-to-chime gaming benchmark. They exclude the live split/keyframe wait and tick scheduling. Approximately five seconds of measured export work plus these waits is consistent with the user's report. Existing logs do not have sufficient stage timing to attribute each historical hotkey press exactly.

Raw results and diagnostic harnesses are under ignored `.local/`: `save-latency-f22740ad66024ba1b8bafa7b303771bd/results.json`, `production-results.json`, `latency-harness`, and `save-latency-investigation.ps1`.

## Recommended correction

1. **Index finalized segments while recording.** Each segment should retain its path, actual video duration, and readiness state. Probe each closed segment once, asynchronously, with bounded concurrency. Release its metadata when deleting the segment. When saving, reuse the index and wait only for any required segment that is not yet ready, normally the newest one. Do not let background failures become unobserved tasks or allow probes to accumulate without bounds.
2. **Process replay events and export completion promptly.** Drain disk events and completion from the existing 50 ms worker loop, or wake the loop when an event arrives. Keep game discovery and health/heartbeat work at the slower interval. Do not run all recorder housekeeping twenty times per second merely to improve save responsiveness.
3. **Preserve the media guarantees.** The OBS file-change signal is emitted after sending the helper its next filename, before the helper is guaranteed to have finalized the preceding MP4. Keep bounded finalization retries. Retain precise video durations: generic container duration can include AAC padding and accumulate drift across joins. Preserve four audio tracks, keyframe alignment, cancellation, protection against existing destination names, active-export segment retention, and temporary-file publication. Background inspection must not block capture or race deletion/shutdown.
4. **Measure the corrected live path.** Add elapsed-time logging for request, split event, readiness, assembly, verification, publication, and saved notification. Test full 30-second, two-minute, and five-minute buffers; repeated saves; recording under game load; and RAM versus disk. Verify decode, packet preservation, track alignment, endpoint timing, cancellation, and bounded cleanup. Test on SSD and HDD separately before making latency promises.

The first two changes remove the main avoidable costs without giving up disk buffering's RAM savings. They should remove roughly 3.5 seconds of repeated inspection in this two-minute fixture, plus most tick scheduling delay. A new end-to-end benchmark is required to establish the final result. Writing a roughly 367 MB output and waiting for a keyframe still take time.

Do not change the success chime to fire before publication. Do not globally shorten the encoder's keyframe interval as the first fix; that changes compression behavior and does not address the measured repeated inspection. Faststart and the final verification are secondary costs; optimize them only after measuring the corrected pipeline and checking playback compatibility.

For immediate use, the existing **Settings → Save replay in RAM** switch bypasses the segmented disk export path, at the cost of a larger RAM buffer and a fresh buffer on switching. No setting was changed during this investigation.

Application source and release version were left unchanged during the investigation above.

## Implementation follow-up

Implemented background indexing with one probe worker and a bounded 160-segment queue. Each retained segment owns its inspection result; exports wait for pending results and reuse completed results. Probe failures enter recorder recovery, while disposal cancels and joins inspection/export before session cleanup. File readiness retries and precise video packet timings are preserved.

Save processing now runs from the worker's existing 50 ms loop. Game discovery, health checks, and heartbeats keep their one-second interval. Added split, readiness, assembly, verification/publication, and notification elapsed-time logging. The smoke diagnostic now uses the same polling cadence and records request-to-saved elapsed time.

All 19 buffer checks and 15 media checks passed. New recording-path cases cover delayed segment finalization, repeated indexed saves, complete multitrack decoding, and disposal during a pending inspection/export. The Windows self-contained build passed without warnings or errors.

The same two-minute export using pre-indexed timings measured 1.469, 1.696, and 1.813 seconds, versus the original 5.123–5.329 seconds. The first new output's SHA-256 matches the first original output exactly. These are export-component measurements, excluding the keyframe wait and live frontend notification. No release was published and installed settings were not changed.

Live capture diagnostics using both the default display and the configured DISPLAY2 stopped at the recorder's existing selected-display-unavailable check before the save request. Live hotkey-to-chime measurement therefore remains unverified in this session and should be tested in the user's running app.
