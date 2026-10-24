# Tasks

- [x] Step 1: Mp4Muxer progressive mode + journal + Recover + tests
- [x] Step 2 (b9591c5): Engine/ClipWriter.cs -> incremental writer (AddVideo/AddAudio/Finish), Write stays a wrapper; existing tests green
- [x] Step 3 (73abefc): Engine/RecordingSink.cs (queue + "Clink.Recorder" writer thread, byte budget max(64 MB, 15 s), drop-to-keyframe backpressure, limits, stop with audio catch-up <=800 ms), Engine API (StartRecording/StopRecordingAsync/IsRecording/Recording status/RecordingStopped event/RecoverRecordings), _tapGate fan-out at Engine.cs:172, AudioTrack.FrameTap, detach taps before TearDown, Dispose waits <=15 s; SelfTest --record; tests with fake producers
- [x] Step 4 (93af45d): "record" hotkey action, tray item, --record command; AppController.Recording.cs + RecordingPolicy (pure restart rules, tests); RecordingStopped marshalled with BeginInvoke; red status dot + "Recording mm:ss"; settings restarts postponed; continuation parts; queued start; startup recovery with single toast; bounded stop on session end/exit
- [ ] Step 5: recordings folder + max part length settings (+ SettingsImport categories); separate toast switch for recordings; scan `Recordings\*.mp4` in ClipLibrary.Load (also fixes recordings lost during an import restart) + IsRecording badge in ClipsPane; "Recording saved" toast totals across parts + Open folder; recovered entries: take time from file name; Trimmer seek-and-stream or selection cap before trim is offered on recordings; archive opt-in decision; engine-owned run id instead of controller `_engineRunGen` (slow starts >15 s); count pending technical restarts instead of clearing in StartEngine (back-to-back display events can create a short extra part); `StartEngine` returns whether it issued a start; `--record` at cold start in game-only mode should stay queued until the first game poll; wording after 3 empty automatic parts when earlier parts were saved; controller sequencing tests; release note: older Clink treats a "record" hotkey as a clip
- [ ] Manual soak: 2 h recording, clip during recording, kill process -> recovered on restart, display change -> part 2

## Blocked / needs user
- Live app test not run because the user's installed Clink was running; run the manual test script in STATE.md.
- Product decision pending: continuing a recording after an arbitrarily long sleep.
