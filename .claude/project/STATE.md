# State

## In progress
Manual start/stop recording (branch `feature/manual-recording`).
Step 1 done: progressive Mp4Muxer + journal/Recover (commit 3855739).
Verified: Debug build 0 warnings; `--unittest` 321 passed/0 failed/0 skipped (also with CLINK_TESTS_NO_SKIP=1 and CLINK_EMULATE_ELST=1).
Step 2 done: incremental ClipMuxWriter (commit b9591c5); ClipWriter.Write is a wrapper, clips byte-identical (golden SHA-256 tests). Verified: build 0 warnings; 330 passed/0 failed/0 skipped.

## ClipMuxWriter API
ctor(ClipOutput FastStart(path) | Progressive(inProgress, final), ClipVideoConfig(Codec, Width, Height, Fps, SequenceHeader?), IReadOnlyList<AudioTrackDesc>, origin100ns, aligned). AddVideo(pts, key, ReadOnlySpan<byte> annexB); AddAudio(track, in AacFrame); NextVideoPts; Flush(durable); ClipMuxResult Finish() (Path, Duration100ns ~ video span, SizeBytes, AudioTracks, VideoSamples, AudioSamples[]); Discard(); Dispose() keeps .rec/.idx. Properties: HasVideo, VideoElapsed100ns, DurationLimit100ns. Single-thread. No monotonic-pts guard: the sink owns it. Progressive mode creates no files until parameter sets are known.

## Key decisions
- moov-at-end + sidecar journal chosen over fragmented MP4: keeps box layout identical to clips so Trimmer/ClipSource/ClipAlignment/clk1 brand work unchanged; fMP4 would risk MF edit-list and seeking behaviour.
- Recording shares the replay encoder (quality = replay quality).
- Recording starts from the ring's last keyframe at or before the hotkey (<=1 GOP pre-roll), no forced IDR.
- Engine restarts (settings change, display change, sleep, pause) end the current part; controller starts a new part.
- Recordings land in `<ClipsFolder>\Recordings\`; in-progress files use `.mp4.rec` in the final directory.

## Sink contract (Mp4Muxer progressive mode)
- Single owner thread for Write*/Flush/Finish/Discard/Dispose.
- Flush(false) every 1 s, Flush(true) every 5 s (contract updated from 10-30 s to 5 s).
- Know avcC before CreateProgressive (SPS/resolution change => new file).
- Stop before DurationLimit() with a margin using VideoElapsed100ns, or catch DurationLimitException then Finish.
- Any other Write* exception => stop writing, Dispose, Recover.
- Finish throws => do not retry on the instance; Dispose then Recover.
- "Nothing recorded" => Discard.
- Startup recovery: Recover each *.rec with .idx not currently live (IOException => in use, skip; null => keep files, don't delete); delete orphan .idx without .rec; delete header-only .rec without .idx.
- Ensure the final directory exists; read FinalPath after Finish.

## Known limits
- Index memory ~20-40 MB/h (cap parts at ~4 h).
- 32-bit duration limit ~13.25 h.
- Trimmer loads whole selections in memory (unsuitable for hours-long files until seek-and-stream).

Step 3 done (commit 73abefc): RecordingSink + Engine recording API + SelfTest --record. Verified: build 0 warnings; 360 passed/0 failed/0 skipped (also CLINK_TESTS_NO_SKIP=1); real `--selftest 10 --record` exit 0, 10.47 s HEVC + 5 AAC tracks, dropped video frames 0, clip saved mid-recording OK.

## Engine recording API (Engine.cs)
- `public bool IsRecording`
- `public RecordingStatus? Recording`
- `public event Action<RecordingResult>? RecordingStopped`
- `public string StartRecording(RecordingOptions options, string? gameHint = null)`
- `public Task<RecordingResult> StopRecordingAsync()`
- `public static IReadOnlyList<RecordingResult> RecoverRecordings(string folder)`

## Sink behaviour (RecordingSink)
- Writer thread "Clink.Recorder"; encoder/audio threads only copy into pooled buffers and enqueue. Starts at ring's last keyframe via shared gate.
- Byte budget max(64 MB, 15 s at bitrate); drop-to-keyframe backpressure; Overflow stop after 30 s of sustained overflow.
- Discontinuity if gap >5 s. MaxDuration default 4 h, also limit-60 s of the 32-bit mux duration limit.
- DiskFull when free space <1 GB, checked every 5 s.
- Flush(false) 1 s, Flush(true) 5 s.
- Clean stop: <=800 ms audio catch-up, audio cut at endPts for every track.
- Taps detached before teardown; Dispose waits <=15 s for an in-flight finish.
- Recovery never deletes files it cannot positively identify.

## Residual risks
- Hung disk I/O cannot be interrupted (Dispose waits <=15 s then proceeds).
- L3 untested (no seam).
- H1 shutdown race covered by code reading only.
- Real-file recovery not yet exercised; leftover killed pairs for a manual test are in C:\Users\Jason\AppData\Local\Temp\claude\C--Users-Jason--claude-spares-spare-RBufNV\181a9c26-3628-401c-b06d-642afbeb070d\scratchpad\rec5 and ...\scratchpad\rec7_1500.

Step 4 done (commit 93af45d): AppController.Recording.cs + RecordingPolicy.cs, hotkey/tray/--record. Verified: build 0 warnings; 368 passed/0 failed/0 skipped (also CLINK_TESTS_NO_SKIP=1). Live GUI test not run.

## Controller behaviour (Step 4)
- Toggle: starts if idle, stops if recording; during the gap between parts or while a start is queued, toggle cancels/stops rather than starting a second recording. A start requested while the recorder is starting is queued (30 s).
- Policy (RecordingPolicy, keyed on engine run generations): new part for EngineStopped/MaxDuration/Discontinuity, plus autoPart; none for UserStopped/DiskFull/WriteFailed/Overflow/Empty/Unrecoverable. Continuation window 180 s; technical-restart deadline = restart delay + 10 s.
- Settings-driven engine restarts are postponed until the recording ends. Display change, resume from sleep, GPU loss continue into a new part.
- Finished file added to library tagged IsRecording (share-last and trim-last skip it).
- Status dot red with "Recording mm:ss" from IsRecording/Recording status.
- Recovery of abandoned recordings at startup and on folder change, off the UI thread, single toast.
- Exit/session end: StopRecordingAsync with 4 s bounded wait.

## Manual test script (Step 4)
1. Press the record hotkey: toast, click sound, red dot, "Recording mm:ss".
2. Press it again: stop toast, file in <ClipsFolder>\Recordings, appears in library.
3. Start a recording, then save a clip with the clip hotkey: clip saved, recording continues.
4. Start/stop from the tray item; also `Clink.exe --record` toggles.
5. During recording change a setting that restarts the engine: restart is postponed until the recording stops.
6. Pause capture while recording: recording ends.
7. Start recording, kill the process, relaunch: file recovered, exactly one toast.
8. Start recording, change the display (resolution/monitor): part 2 starts.
9. Refusals: start while paused/no game (game-only) or low disk gives a clear toast, no recording.
10. Quit Clink while recording: file finalized (within ~4 s) and playable.

## Next action
Run the manual test script; then Step 5 (see TASKS.md).