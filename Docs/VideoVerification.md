# Native video verification lifecycle

The first batch verification decoded all six MP4 files, then Unity 6000.0.85f1
crashed. The saved native stack included `VideoPlayer::VideoUpdate` and Mono
callback invocation. The checker had been stopping/destroying players and creating
their successors inside VideoPlayer native callbacks, which was an unsafe lifecycle.
The stack supports locating the failure in video playback; it does not establish
that every possible engine/driver cause has been eliminated.

`DatasetBatchVerifier` now treats Prepared/FrameReady/Finished/Error callbacks as
state notifications only. Playing, GPU readback, stopping, event unsubscription,
destruction and advancing to the next file occur from EditorApplication.update.
Play-mode objects are destroyed normally rather than with DestroyImmediate, and
there is a short teardown gap before preparing another decoder.

After this change, the same six files (678 frames total) were fully decoded again.
The editor remained responsive after completion and exited Play mode normally.
The independent batch renderer had already completed before the crash, so its
MP4 outputs, metadata and manifest were retained.

The encoder generates MP4 through Unity MediaEncoder. Windows Media Foundation can
emit a color-primaries warning for these files; decoding tests do not constitute
colorimetric or physical-contact validation. For large unattended QA workloads,
using a separate decoder process would further isolate native playback failures.
No crash logs were uploaded to an external service.
