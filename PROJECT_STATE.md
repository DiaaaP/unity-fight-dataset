# Project checkpoint

Latest addition, 2026-11-18: native per-frame YOLO Pose export is implemented.
`exportYoloPose` writes JPG + COCO17 TXT pairs under each new batch's yolo folder,
with data.yaml, grouped splits and geometric 0/1/2 visibility. It reuses the RGB
baked meshes; facial surface anchors support both supplied models, including the
one without eye bones. Review landmarks when adding new models. Partial pairs
are staged and archived on resume. Historical descriptions below are dated;
YOLO export is no longer deferred. Read `Docs/DatasetGenerator.md` and
`DatasetOutput/Checks/YoloPose_20261118/verification.json` for current details.
Final checks: Unity compilation 0 errors/warnings; 5 independently decoded MP4s,
634 JPG/TXT pairs, 1,268 person rows with 56 fields each. All 250 paired async/sync
labels were identical after removing the projection round trip from visibility
rays. Front/rear/self/other-person/opaque/alpha-cutout and out-of-frame probes
passed. Pause stayed at frame 48; stop/resume archived 48 unfinished pairs and
completed without duplicates. No generator job or transient probe meshes remain.
Saved user config retains 100 per Hit/Kick, one view, 1280x720 at 25 FPS and async
readback; only exportYoloPose=true / poseJpegQuality=90 were added. Older batches
must be regenerated for exact synchronous YOLO annotations.

Unity project: `C:\Users\D\Documents\Unity\test`.
Unity 6000.0.85f1; use CLI auto-discovery (most recently port 7800; do not pin an old port).
Use the installed local unity-cli skill and terminal; no screen/mouse automation.

## Generator implementation

- Existing two-character test and its corrected CPU-skinned exporter are preserved.
- New generator code: `Assets/Scripts/Dataset/` and `Assets/Editor/Dataset*.cs`.
- Automatic Humanoid import, folder/name classification and stable catalog entries.
- Catalog: `Assets/Config/AnimationCatalog.asset`.
- Batch config: `Assets/Config/DatasetGeneratorConfig.asset`.
- Separate four-wall preview scene: `Assets/Scenes/FightDatasetPrototype.unity`.
- Local seeded hit/kick/fight/normal scheduling, available-clip selection, spacing
  estimates, smooth blending, static synchronized camera views and CPU mesh baking.
- Async local batch output includes MP4s, metadata, grouped split manifest,
  quality/failure reporting and optional raw rig projections (not training-ready).
- Full user instructions and limits: `Docs/DatasetGenerator.md`.

## Actual supplied assets

Two characters, Punch_1, Idle, Medium Hit To Head. No actual Kick, Block or Dodge
clips are currently supplied. Kick export cannot be visually validated until a real
kick is imported. Fall/GetUp are recognized but excluded from automatic sequences.
Normal negatives currently consist of Idle unless more neutral clips are added.

## Checks performed

- Unity compilation: 0 errors, 0 warnings.
- Planner validation: 12 scenarios, 692 sampled poses, 36 camera plans; deterministic
  repeat and random-time sampling checks passed for hit/fight/normal.
- Temporary copied FBX verified automatic Humanoid import and name classification;
  manual classification/impact overrides survived catalog refresh. Test copy removed.
- Three-scenario/two-view 720p smoke batch completed: 3 scenarios, 6 MP4s, 0 renderer
  failures, with optional raw joint projections. Directory:
  `DatasetOutput/Generated/batch_20261029_152525_154_9eaa9e/`.
- All six MP4s decoded, 678 frames total; dimensions, duration, timestamps and split
  groups checked. Selected decoded hit/fight frames visually inspected.
- The first checker caused a native VideoPlayer crash after decoding. Lifecycle
  operations were moved outside native callbacks, and the same six-video check
  completed again with the editor remaining responsive. See VideoVerification.md.
- Four sequential attacks exercised switching between two independent clip objects
  (one temporary in-memory copy, not a new claimed dataset asset), 233 sampled poses.
- Final limited batch after the last generator changes:
  `DatasetOutput/Generated/batch_20261029_154526_269_f8d6fc/` — 1 hit scenario,
  2 views, no generation failures; both 100-frame MP4s decoded successfully.
  Total distinct checked outputs across these batches: 8 videos / 878 frames.
- Optional projection files checked: 6 JSONL files with matching 100/139 frame
  counts, two stable actor IDs and 17 named slots, training_ready=false and
  occlusion_checked=false.
- Final Editor state: responsive, Edit mode, generator scene active, no decoder
  objects remaining, Run In Background restored off. Current-session console had
  no error entries. The original test scene remains saved on disk.

## Preservation and boundaries

No Git repository existed. Pre-change scripts/test scene/docs were archived in
`Backups/BeforeDatasetGenerator_20261029_201256.zip`.
SampleScene originally had unsaved edits. After the user-confirmed editor crash and
restart it reopened clean; do not claim that prior unsaved state was recovered.
It was not edited or overwritten by the generator.
Dataset output and backups are ignored by .gitignore and stay outside Assets.
The original prompt's ML/training stage is deferred; no trained classifier exists.

## Main files added/changed for this milestone

- `Assets/Scripts/Dataset/AnimationCatalog.cs`
- `Assets/Scripts/Dataset/DatasetGeneratorConfig.cs`
- `Assets/Scripts/Dataset/DatasetScenarioPlayer.cs`
- `Assets/Editor/DatasetCatalogTools.cs`
- `Assets/Editor/DatasetSceneFactory.cs`
- `Assets/Editor/DatasetScenarioPlanner.cs`
- `Assets/Editor/DatasetBatchExporter.cs`
- `Assets/Editor/DatasetProjectedJoints.cs`
- `Assets/Editor/DatasetBatchVerifier.cs`
- `Assets/Editor/DatasetGeneratorChecks.cs`
- `Assets/Editor/FightTestVideoVerifier.cs` (safe video cleanup)
- `Assets/Config/AnimationCatalog.asset`, `DatasetGeneratorConfig.asset`
- `Assets/Scenes/FightDatasetPrototype.unity`
- `Assets/Materials/DatasetGenerator/Floor.mat`, `Walls.mat`
- `.gitignore`, `START_HERE.md`, this checkpoint, and generator/verification docs.

## Fall correction — 2026-10-29

`Shoulder Hit And Fall.fbx` was accidentally classified as Stumble because of its
folder. Its older import settings extracted vertical root motion while playback
disabled root motion: the final hips stayed around 1.1 m above the floor.
Refresh now repairs fall imports, recognizes Fall before Stumble, and preserves
downward motion in the pose. Reviewed falls can appear only as the final reaction;
their final pose is held without blending to Idle. Mesh penetration is corrected
vertically for differences between retargeted character bodies.

Only Shoulder Hit And Fall currently has Reviewed Fall enabled. Other falls and
GetUp remain excluded pending individual review. Metadata records held falls with
`holds_fallen_pose=true`, `idle_return_seconds=-1`; the decoder understands this.

`DatasetFallChecks.Validate()` passed 624 sampled poses, both character models,
0.8/1.0/1.2 speeds, end-pose retention and sampling-order checks. Final hips were
below 0.175 m and final mesh bottoms at 0.002 m. Compilation had no warnings/errors.
Two replacement 131-frame videos passed actual MP4 decoding and final decoded-frame
inspection. The corrected complete 20-video package is:
`DatasetOutput/Generated/batch20_20261029_164436_c1ce6a_fallfix/`.
Its report combines the unchanged 18 successful decode results with the two newly
checked replacements. Original outputs remain preserved. Backup before edits:
`Backups/BeforeFallFix_20261029/`. Editor returned to Edit mode, background override off.

## Progress dashboard and recovery — 2026-10-29

Added UI Toolkit dashboard at Tools > Fight Dataset > Generator > 0 Generation
Progress, automatically opened by batch start/resume. Two progress bars show
batch/scenario-frame progress; counts show completed videos, completed scenarios
and failures. Controls: Pause, Resume, Save And Stop, resume from a selected folder,
open output folder and select config. Closing the window does not stop generation.

Live pause retains encoders and continues from the exact frame. Script reload,
editor quit, Play or catalog refresh suspend safely. New batches save portable
`resume.json` with frozen config/catalog settings and source fingerprints;
`manifest.json` atomically commits completed scenarios with next_scenario_index.
After restart the unfinished scenario is rendered again from frame zero, preserving
committed outputs. Interrupted attempts are archived with `.interrupted` suffixes.
Changed source assets/code, missing committed files and inconsistent manifests
are rejected. Older batches without resume.json cannot use recovery.

Validation: DatasetResumeChecks exercised actual UI buttons, held frame 5 across
eight Editor updates, resumed, then paused scenario two at frame 9 after committing
scenario one. Forced compilation/domain reload invoked suspension and destroyed
static state. Resume via the dashboard restored the same directory and original
3-scenario/2-view/320x180/10fps settings while the saved user config remained
10 scenarios/2 views. Committed MP4/JSON SHA256 values stayed identical and the
restarted scenario's first nine pose signatures matched exactly. Six projection
JSONL files each contained 30 sequential frames; no duplicate or partial MP4s.
Invalid source fingerprints were rejected before modifying the manifest.

Report: `DatasetOutput/Checks/progress_resume_test.json` (passed).
Test output: `DatasetOutput/Generated/batch_20261029_171347_889_b943aa/`.
All six MP4s decoded successfully, 180 frames total. UXML/USS imported without
parse warnings; current compilation has no errors/warnings. Before-change backup:
`Backups/BeforeProgressResume_20261029/`. Original 20-video fallfix output is intact.

## Async GPU frame export — 2026-10-29

Use Async GPU Readback is enabled in the saved config. Added
`DatasetFrameReadbackQueue.cs` and `DatasetBatchExporter.Readback.cs`: bounded
three-frame FIFO, dedicated render targets/persistent native RGBA32 buffers per
view, ordered MediaEncoder native-buffer writes, pose-aligned projection/inspection
capture, draining before scenario commit. Completion callbacks only retain stable
flags; request handles themselves expire. Requests are polled with Update() in
Edit mode with forcePlayerLoopUpdate=false. Active export signals Editor ticks to
avoid background throttling; pause/finish stop those signals. Outstanding native
writes are completed before freeing resources at stop/reload. GPU errors reject
the uncommitted scenario and select synchronous capture for the remaining batch;
unsupported hardware or the config toggle also selects synchronous capture.

Validation reports:
- `DatasetOutput/Checks/async_readback_pixels.json`: 6 frames x 2 views,
  2,764,800 bytes matched synchronous ReadPixels exactly, bounded queue/reuse,
  delayed consumption and disposal with pending writes passed.
- `DatasetOutput/Checks/async_readback_benchmark.json`: identical 8 hit/kick
  scenarios / 16 videos at 1280x720 20 FPS, 1,007 poses/timestamps and 64 inspection
  PNGs matched. Same wake-up policy: sync 14.178 s, async 10.691 s (1.33x / 24.6%
  less time). Earlier runs varied with background Editor throttling; the initial
  41.374 s baseline is not the isolated async gain.
- `DatasetOutput/Checks/progress_resume_test.json`: passed with async capture;
  pause held encoded frame 6 and three queued frames, resume and actual domain
  reload recovered the same batch without changing committed output hashes.
  Replayed poses and six sequential 30-frame projection streams matched.
  Test batch: `DatasetOutput/Generated/batch_20261029_174851_390_6c015f/`.

Final async benchmark output:
`DatasetOutput/Benchmarks/AsyncReadback/batch_20261029_174710_382_575cae/`.
Synchronous control:
`DatasetOutput/Benchmarks/AsyncReadback/batch_20261029_174729_102_802b8a/`.
Before-change backup: `Backups/BeforeAsyncReadback_20261029/`.
The user's completed batch `batch_20261029_172430_380_818421` remains intact:
90 completed scenarios / 270 videos; 10 rejected scenarios, no active work when
the async change was applied.

Independent final video verification passed:
`DatasetOutput/Checks/async_readback_decode.json` / `verification_pyav.json` in
the async benchmark and recovery test folders. PyAV/FFmpeg decoded all 22 output
videos / 2,194 frames, validating dimensions, FPS and every presentation timestamp.
All 2,014 benchmark frames were compared to the synchronous control: 1,995 were
RGB-identical; 19 had tiny differences (worst mean absolute RGB error 0.002679/255,
worst RMSE 0.09322/255, PSNR above 68 dB). The 64 raw inspection PNGs were exact
matches. No claim of bit-identical compressed video is made.

Unity VideoPlayer verification timed out twice at the first MP4 while the Editor
continued ticking; its failed `verification.json` is retained, not relabeled as a
pass. Independent decoder dependencies were installed only under the ignored
backup folder `Backups/BeforeAsyncReadback_20261029/video_check_deps/`; the project
has no new runtime/package dependency. Final Editor state: Edit mode, no running
export/decoder, no readback render targets, Run In Background override off.

## Visible strike contact and camera diversity — 2026-11-07

Replaced raw-clip/proxy impact placement with DatasetContactSolver and
DatasetContactGeometry. Calibration samples the actual blended Playables pose at
output frame times. Weighted hand/foot surface vertices are tested against the
defender's baked mesh triangles; maximum accepted surface gap is 0.008 m. The
exporter rechecks contact at the encoded impact frame. Metadata records the frame,
surface points, gap and mesh_contact_verified. Invalid clip/speed/stance combinations
are rejected; later strikes in a fight must reach from the same stance. Reactions
start one output frame after contact; Block/Dodge are excluded from contact classes.
Use single-strike source clips. This is geometric contact validation, not physics
simulation or pixel-accurate occlusion labeling.

DatasetCameraPlanner samples continuous azimuth, height, distance, field of view,
aim and small roll within six families: eye_level, low_angle, high_angle, overhead,
over_shoulder and wide_room. Wide/medium/close full-body framing fits both actors'
sampled motion envelope; coarse impact occlusion checks reject obscured contacts.
Each MP4 keeps one camera. Hashed seed selection avoids the old category/seed-stride
correlation with camera family. Added a ceiling for low-angle views. Saved config
uses camera height 0.25–3.35 m, FOV 35–90 degrees and contact tolerance 8 mm;
three views, 1280x720/20 FPS and async capture remain configured. Final live config
has 15 scenarios and Hit/Kick enabled; preserve these current user settings.

Validation: contact_camera_checks.json passed, 164/164 cases completed: 108
single-clip/speed trials (89 accepted, 19 rejected safely), 48 camera scenarios /
144 views across all six families, and eight valid multi-strike sequences. Final
camera regression checked 7,287 framing samples; sampled heights 0.282–3.346 m,
FOV 35–87.631 degrees. Existing progress_resume_test.json passed with pending async
frames, exact live pause, actual domain reload, deterministic replay and unchanged
committed files (six videos).

Review bundle: DatasetOutput/ContactCameraReview_20261107/contact_camera_review_ready/
contains 27 completed MP4s, nine scenario metadata files, inspection images and a
passed independent PyAV/FFmpeg verification: all 3,228 frames decoded, dimensions,
FPS and every presentation timestamp checked. Maximum measured strike gap 2.87 mm.
Examples cover all six camera families. Its source batch retained three rejected
attempts: one storage replacement failure (subsequently fixed), two floor/wall
geometry failures. The clean bundle preserves those reasons in its manifest.

Fixed intermittent Windows File.Replace failures for checkpoints/manifests with
same-directory MoveFileExW(REPLACE_EXISTING | WRITE_THROUGH), bounded sharing-error
retries and fatal commit-error propagation. File-lock/release retry was exercised.
No new Unity package dependencies. Backup: Backups/BeforeContactCameraFix_20261107/.
Start a NEW batch with this version: old videos are unchanged and require
regeneration to obtain these fixes. Source fingerprints intentionally prevent
resuming an older partial batch with changed generation code. The user's old
batch_20261030_091359_007_f04dbd was not modified.

## Weighted surveillance views, contact regions and animation balance — 2026-11-09

User requested predominantly upper-room camera angles with rare low angles,
fewer apparent misses, livelier timing and less repetition of individual strikes.
All work used terminal/Unity CLI; no desktop screenshots or mouse automation.
Backup: Backups/BeforeWeightedCamerasContact_20261109/. CLI beta.14 reports beta.17
and optional Codex setup; neither approval-gated remediation was run.

Camera config now exposes relative weights: high 45, overhead 15, high wide-room
15, eye-level 15, shoulder 5, low 5. Each view samples the weighted hash separately,
including when Camera Views is one. Continuous position/zoom/orientation diversity
and view separation remain. Contact visibility uses double-sided actual mesh
segment tests against both actors, with 7 cm endpoint clearance. It is a geometric
guard, not alpha-aware pixel visibility/YOLO labeling.

Contact probes now restrict target triangles by exact humanoid bone groups:
guard hands/arms cannot stand in for a head/chest impact. Calibration selects the
dominant moving hand/foot and rejects idle/recovery-phase touches; Verify also
checks approach direction/speed on the actual output-frame pose. The 8 mm surface
gap limit remains. Per-action metadata includes approach_speed_mps and the named
target region. Reactions still start one frame after impact.

Planner gives priority to the least-used compatible animation in committed
scenarios, shuffles ties and tries up to three speeds before fallback. Usage is
counted per action, not per camera, committed/rolled back with the scenario, and
rebuilt from committed metadata on resume. Manifest animation_usage and final
animation_usage.csv expose completed actions, attempted variants and rejections;
per-scenario animation_selection records reasons. This cannot promise equal use
of geometrically incompatible source clips. No catalog clips were disabled.

Independent neutral offsets/speeds, randomized lead-in/recovery gaps, blend times
and reaction speed reduce synchronized timing. Reviewed falls have 15% selection
chance when standing reactions are available. Source motions remain unchanged.
General visible-mesh floor correction now covers kicks/idle/reactions as well as
falls, only lifting penetrating poses. Diagnostic export found 11 kicks failing
the floor bound before this fix. GetActorLocalPosition exposes the stable base
pose so temporary grounding offsets cannot accumulate during solver retries.
Encoded-frame floor tolerance was tightened from 18 cm to 1.5 cm.

Final contact/camera regression: DatasetOutput/Checks/contact_camera_checks.json
passed 131/131 cases: 25 enabled source attacks at three speeds, 48 scenarios /
144 camera views and eight multi-strike sequences. 54/75 clip/speed combinations
were accepted, 21 safely rejected, covering 20 distinct source attacks. Accepted
cases also test anatomical false positives, mesh-ray visibility, base-position
stability and sample-order independence. Framing checks: 7,245. A 30,000-view
distribution test measured 74.82% upper / 4.903% low; the least-used ordering test
kept compatible synthetic usage counts within one.

Final review export:
DatasetOutput/WeightedCameraReview_20261109/batch_20261109_163736_476_8e98dc/
completed all 40 requested scenarios / 40 MP4s, zero rejected scenarios. Independent
PyAV/FFmpeg decoded all 5,596 frames and checked dimensions, FPS, every PTS, contact
proof/approach direction metadata, selection logs and usage totals. Camera views:
20 high, five overhead, two wide, seven eye-level, three shoulder, three low
(27/40 upper views; small batches vary around the configured probability).
Maximum measured gap 6.44 mm, minimum approach speed 0.117 m/s. All 40 scenarios
have distinct independent neutral phase pairs. Twenty source clips were used:
seven punches occurred 2–3 times each, thirteen kicks 1–2 times each. Thus counts
among accepted clips differ by at most one within each action class. Kick_11,
kick_12, kick_17, Punch_6 and Punch_7 failed all trials in this review and were
excluded with logged reasons, not disabled in the source catalog. The diagnostic
pre-grounding batch with eleven failures remains intact and is not the final review.
Actual decoded impact frames were visually inspected alongside raw inspection PNGs.

Verification: DatasetOutput/Checks/weighted_camera_video_verification.json and
verification.json in the final review folder. Its animation_usage.csv exposes the
counts above. User config remains 15 scenarios, one view, 1280x720, 25 FPS,
Hit/Kick enabled, original seed/output folder, async on; only camera probability
fields were added. New batches are required; prior files are not rewritten.

Recovery regression also passed using ATTACK scenarios (not just neutral scenes):
DatasetOutput/Generated/batch_20261109_164251_015_fa25c6/. Exact live pause held
frame six with pending async frames, then an actual script domain reload suspended
the second scenario. Resume preserved committed file hashes, replayed the same
pose prefix and restored animation usage. Three scenarios chose three distinct
compatible punches. Six recovered MP4s / 336 frames decoded independently with
valid timestamps. Report: DatasetOutput/Checks/progress_resume_test.json; per-batch
verification.json. Final Editor state: ready in Edit mode, no export running,
saved user settings confirmed as 15 scenarios / one view / 25 FPS / async enabled.

## Count per enabled category — 2026-11-09

Scenarios Per Category now means the number for EACH enabled, available category:
100 with Hit/Kick plans 100 hit + 100 kick scenarios. Total MP4 count also multiplies
by Camera Views. Field renamed to scenariosPerCategory with FormerlySerializedAs
migration and a compatible scenarioCount C# property; Inspector display name and
the retained user value (15) were verified through SerializedObject. Saved config
remains 15 per category, one view, 1280x720/25 FPS, Hit/Kick and async enabled.

Exporter computes the combined requested_scenarios once; all stop conditions,
frame status/progress denominators and resume validation use that total. Manifest
adds count_mode=per_category, scenarios_per_category and total_scenario_limit.
Smoke still plans one scenario per category / two views. The optional API limit
continues to cap the OVERALL total. Updated the existing 40-video review helper
to request 20 per category. Existing completed output folders are unchanged; old
partial checkpoints remain source-incompatible and require a new batch.

Compilation: zero errors/warnings. DatasetCategoryCountChecks verified a paused
100+100 plan and resumed it without rendering 200 videos, retained smoke totals
and an overall cap of three, then actually rendered two hits and two kicks with
Save And Stop / Resume after the first committed scenario. Final 4/4 outputs,
two per class, zero failures. Report DatasetOutput/Checks/per_category_count.json;
test output DatasetOutput/Checks/PerCategoryCount/batch_20261109_165329_547_f5fa70/.
Backup: Backups/BeforePerCategoryCount_20261109/. No main 100+100 run was started.

## User-reported misses and reaction timing — 2026-11-10

User supplied hit_00007 / hit_00025 (apparent misses), hit_00053 (late response),
hit_00151 (early response), and screenshots naming kick_00026_s113075807 and
kick_00120_s122920333. Exact source batch found:
DatasetOutput/Generated/batch_20261109_165453_010_920226/. Original files/metadata
were left intact. Backup: Backups/BeforeStrikeTimingFix_20261110/.

The prior v2 check was insufficient: a tiny geometric gap at a selected frame
could occur while blending up from Idle, or during a late returning motion.
Observed source impact positions were 13% for hit_00025, 12% for hit_00151,
and 65% for hit_00053. Decoded full-motion contact sheets confirmed that these
times did not represent the main strike. Historical numeric v2 passes must not
be interpreted as validation of strike/reaction timing.

Added DatasetStrikeProfile: samples the source clip without Idle blending,
identifies outgoing stroke bursts and main extension, determines the striking
hand/foot and source direction, and can cut a later significant outward burst.
Profiles are cached per clip/avatar/scale. Per-action runtime yaw smoothly aligns
sideways source strokes to the opponent. Sampling resets stable base rotation
as well as base position. Attack fade is adapted so contact occurs after blending
in. Contact search is limited to the main stroke near its extension, and finds
the first valid MAIN contact; incidental setup touches do not trigger response.
Close hooks can use a 0.45 m stance; torso-overlap/room/ground checks remain.

Hand geometry uses palm/proximal knuckles, excluding distal fingertip-only or
wrist/sleeve touches. Both hands and feet use the leading surface in their actual
incoming movement direction; a trailing edge cannot stand in for the strike.
Chest region no longer includes Neck weights. Default maximum gap remains 8 mm.

Added DatasetReactionTiming: reaction weight is zero at the actual impact frame.
Per-action 0.08–0.12 s fades and a calibrated leading-source offset make visible
head/torso movement follow within 1–2 output frames. Actual blended poses are
compared with a suppressed-reaction baseline; no response is allowed one frame
before impact. Checks run during planning AND at the encoded impact frame.
Metadata v3 records source stroke interval/peak/end, runtime yaw, action fade,
incoming direction, ignored preparation contacts, reaction source offset/fade
and measured visible response frame. New core files are fingerprinted for resume.
StartBatch can accept an explicit transient settings object for reproducible
reviews without changing the saved user config/catalog.

All six focused regression probes passed with the SAME original source clips,
speed, seed, two models and original reaction clips. Old cases fail the new
contact/timing contract. Corrected impact fractions: hit_00007 0.555, hit_00025
0.446, hit_00053 0.269, hit_00151 0.451, kick_00026 0.406, kick_00120 0.589.
Responses are one frame after contact except hit_00025 (two). No source clip was
silently substituted to make these cases pass.

All six were actually exported at original 1280x720 / 25 FPS; independent
PyAV/FFmpeg decoded all 803 frames and checked dimensions, FPS, every PTS,
frame counts, source/model/seed/speed identities and timing metadata. Maximum
gap in these six is 2.46 mm. Decoded before/after motion/contact sheets were
visually inspected. Review bundle with ORIGINAL case filenames:
DatasetOutput/Checks/StrikeTiming_20261110/review_ready/.
It contains six MP4s, metadata, contact sheets, comparison.csv and verification.json;
it is a review copy, not a resumable production batch. Raw per-case exports and
probes.json / exports.json / video_verification.json remain alongside it.

Unity was starting when the initial eval returned EDITOR_NOT_RUNNING. The same
project/Editor became ready (PID 92644). Later one recompile returned Pipeline
HTTP 400 during refresh; status became ready and the next recompile reported
up_to_date with zero errors/warnings. One post-compile eval briefly lost Pipeline
registration; waiting for ready restored it. Editor did not crash/restart in
these incidents. No remote bug report was sent and no desktop control was used.

After the user interrupted the chat and asked to continue, Editor PID was 99160
instead of 92644. The background regression had stopped at 34/75 source trials;
its partial report was archived and the full check restarted. It passed 131/131:
67/75 source clip/speed trials accepted, eight rejected, 144 camera views and eight
valid multi-strike sequences; 7,098 framing samples. Recovery verification also
passed with actual attack scenarios, exact paused-frame hold, domain reload,
unchanged committed hashes, replayed pose signatures and restored usage counters.
Six recovered videos / 300 decoded frames passed independent MP4 checks.

A final resource audit found retained playing graphs after temporary scenes and
orphaned ground-probe meshes. Added explicit player reset before preview-scene
closure and hooks for scene close/reload/quit. DatasetPlayableLifetime stops the
graph and destroys its outputs/playable subgraphs before graph destruction;
native empty graph containers can still be deferred by Unity. Twenty scene
creation/sample/close cycles returned to zero owned playable nodes after each
cycle (peak four for two neutral mixers); zero ground-probe meshes remained.
146 stale unowned probe meshes from earlier work were removed while preserving
resources referenced by live players. Report: resource_lifetime.json.

An exact pose-hash comparison during resource verification exposed tiny source
profile yaw differences (below 0.0001 degrees) caused by floating-point sampling
at different world origins/cache warm-up orders. The failed strict comparison is
retained as cleanup_pose_equivalence.json. Source-profile sampling now uses a
canonical origin/orientation and restores normal playback afterward. Exact
profile fields AND all source sample coordinates matched across two deliberately
different origins/yaws for all six reported source clips; see
profile_origin_independence.json. No FBX source files were altered.

## Final verification and handoff — 2026-11-11

Resumed after another intentional chat interruption; Editor PID 99092 was initially
starting, then ready. Latest canonical-profile six-case exports had already
completed successfully. Rebuilt review_ready from those latest exports and
independently decoded all 803 frames again: six original source clips/reactions,
models, seeds and speeds retained; responses one or two frames after main contact,
maximum gap 2.46 mm. README.txt in the review folder is now in Russian.

Final code passed a fresh full contact/camera regression: 131/131 cases completed,
67/75 clip/speed combinations accepted and eight safely rejected, 23 distinct
accepted source clips, 144 camera views, eight valid fight sequences and 7,098
framing samples. Reports: DatasetOutput/Checks/contact_camera_checks.json and
DatasetOutput/Checks/StrikeTiming_20261110/video_verification.json.

Fresh recovery regression on final code passed exact pause, actual domain reload,
unchanged committed files, deterministic pose replay and restored animation usage:
DatasetOutput/Generated/batch_20261111_125325_365_ea1ef9/. Its six MP4s / 300 frames
decoded successfully; progress_resume_test.json and resume_video_verification.json
record the results. Final owned playable-node count and ground-probe mesh count
are both zero after the full regression. No export/check is running, Editor is in
Edit mode, and saved user settings remain 100 per category, Hit/Kick, one camera,
1280x720 / 25 FPS, async enabled. No production 100+100 run was started.

Deliverable: DatasetOutput/Checks/StrikeTiming_20261110/review_ready/.
The original 200-video batch was not overwritten. Regenerate it with a new batch
to apply main-stroke/reaction fixes; completed old videos retain their old defects.

## Inter-actor penetration fix — 2026-11-11

User reported hit_00043_s114856200_cam01 in the newer production batch
DatasetOutput/Generated/batch_20261111_130756_292_b5ed2c. A production export was
still running; SaveAndStop preserved 125 completed videos and suspended that batch
before code changes. Those files were not overwritten or fully re-audited. A new
batch is required because generator source fingerprints intentionally changed.
Backups: Backups/BeforePenetrationFix_20261111/.

Restored the exact v3 metadata pose/animation/reaction/idle/yaw for the reported
video. All 125 frames measured: 35 frames exceeded the 20 mm penetration limit,
first at frame 36 (impact was frame 43), maximum depth 98.67 mm. Actual MP4 frames
confirm the interpenetration. Report and contact sheets are under
DatasetOutput/Checks/Penetration_20261111/.

Added DatasetPenetrationGuard with refitted triangle BVHs and bidirectional
vertex/face-centroid sampling. Nearest-surface inward depth plus three-ray parity
majority reject deep intersections, with a normal test for open clothing meshes.
The planner checks preparation, strike, response, blend-out and idle return before
accepting a trial; the exporter repeats validation for every output frame. Reasons
remain in animation_selection. Metadata adds a sampled 20 mm penetration contract;
the guard source is included in checkpoint fingerprints. Source animations remain
enabled. This is approximate mesh validation, not an exact collision solver or a
guarantee against every small/unsampled/non-manifold intersection.

Contact calibration now approaches from a separated stance to first surface
contact; it no longer accepts an unsigned zero distance while the limb is already
embedded. Deeply intersecting impact placements are rejected, including an earlier
main-phase contact found by the timing scan. Near-vertical incoming motion uses
the same forward-direction fallback as coarse placement.

Validation: compilation passed without errors/warnings; five synthetic overlap /
touch / separation cases passed. Fixed-speed catalog grid checked all 25 clips:
2 punch and 7 kick combinations accepted, other combinations explicitly rejected.
Before separated-stance calibration, the strict guard rejected all 25 grid cases;
that diagnostic baseline is preserved in the backup directory. Accepted source
clips vary with seeds, speeds and reaction choices; the grid is not a blacklist.
A previously accepted recorded pose also passed the coarse-then-full frame order.

Initial guarded export: four videos / 513 frames, independently decoded and
visually inspected. Final separated-stance export:
DatasetOutput/Checks/Penetration_20261111/review/batch_20261111_133835_637_3025c5
completed 1 hit (Punch_7) and 1 kick, zero failures. Independent PyAV decoding
verified all 268 frames, dimensions/FPS/PTS, visible motion, contact tolerance,
new penetration metadata and reaction onset within 1–2 frames. Decoded contact
sheets inspected. Reports: video_verification.json, video_verification_initial.json,
reported_case.json, known_volumes.json and catalog.json in the check directory.

Final runtime: no export or check running, Editor remains in Edit mode,
DatasetPenetrationProbe mesh count zero. Saved user config SHA256 matches its
pre-change backup exactly: 100 per category, Hit/Kick, one camera, 1280x720,
25 FPS, async readback. No replacement production 100+100 batch was launched.

## Root motion restoration — 2026-11-11

User identified the leg/start-point problem in the latest Punch_7 test. Diagnosed
the original default clip import: lockRootPositionXZ/lockRootRotation/lockRootHeightY
false, with applyRootMotion disabled in the absolute-time mixer. RootT at source
0.65 s already carried roughly half a metre of authored displacement, while the
posed hips barely moved. Default Foot IK was inspected in a controlled on/off
comparison; no manual foot lock exists in DatasetScenarioPlayer.

Found an import migration gap: DatasetModelImporter already defined the correct
XYZ/rotation Bake Into Pose policy for new assets, but old FBXs predated it and
Refresh only migrated falls. Renamed/extended EnsureMotionImport to repair every
animation FBX during Refresh. All 41 source metadata files backed up before
reimport in Backups/BeforeRootMotionFix_20261111/Assets/Animations/. Core source,
catalog and user config backed up alongside them. Reimport performed through CLI
and Editor AssetImporter APIs, not by hand editing .meta files.

All 41 clip keys unchanged and 25 enabled attacks retained. Source-profile cache
now keys on dependency hash to invalidate trajectories after import settings
change. Contact stance distance/facing now starts from actual posed hip centres,
since the scene root remains a sampling anchor while the body follows baked motion.
Foot IK is unchanged; raw source comparison showed that Punch_7's wide stance is
authored (max foot span ~1.19 m under both old/new settings).

DatasetRootMotionChecks.Compare tested legacy-settings clones against repaired
imports across all 25 attacks, 61 source times each. All pose/bone-length checks
passed. Punch_7 horizontal hip travel: 0.047188 m -> 0.708295 m. Report:
DatasetOutput/Checks/RootMotion_20261111/motion_comparison.json.

Native source-restricted export retained the actual reported Punch_7 and speed
0.975769; original seed was safely rejected twice, accepted seed 114872038. Its
automatic yaw correction decreased from 77.25 to 6.23 degrees. Contact gap 2.83 mm,
response one frame later. Directory: RootMotion_20261111/reported_export/
batch_20261111_135430_811_1d1e47. Additional Kick_12 native export after the posed-hip
stance update: kick_export/batch_20261111_135807_755_e18f9d, seed 114960929,
gap 7.48 mm, response one frame later. Both passed full export guards. PyAV verified
all 254 frames, FPS/dimensions/PTS/motion and exact source assets. Actual decoded
contact sheets inspected. video_verification.json records both video paths.

Replayed repaired Punch_7 at impact after sampling end/start: zero world-position
error across 106 bones; contact and reaction revalidated on final code.
frame_order_check.txt records this. Compilation passed without errors/warnings.
No export running, Editor in Edit mode, penetration/ground probe mesh count zero.
User 100-per-category config SHA256 unchanged. Earlier full regression/acceptance
reports predate these import settings; they are not claimed as current results.
No production 100+100 replacement batch started; old MP4s remain unchanged.

## All-animation audit and terminal position fix — 2026-11-11

User requested the same repair for other animations. Confirmed the shared import
fix already covers the catalog. Detailed comparison of saved FBX metadata:
36 of 41 imports required correction, five already correct. Example horizontal
hip travel: Punch_8 2.8 -> 88.2 cm, kick_6 10.6 -> 196.4 cm, kick_7 13.2 ->
204.5 cm, kick_9 7.6 -> 171.3 cm. Full audit of all 41 clips on both character
models passed 5,002 poses / 82 clip-model pairs, with finite positions and stable
leg segments. Maximum relative segment range ~5%, consistent with avatar stretch.
Reports: RootMotion_20261111/all_clips_both_models.json,
all_animation_fixes.csv, all_animation_audit_summary.json. Audit/rollback sources:
Backups/BeforeAllMotionAudit_20261111/.

Found a second concrete bug: kick_6, kick_7, kick_9, kick_15 and kick_17 have net
endpoint translation. The old fade to Idle pulled them back towards the initial
anchor by 0.5–2 m (kick_7 ~2.02 m). Added DatasetClipDisplacement to sample horizontal
canonical Body Transform displacement at the actual source end and populate
ScenarioAction attack/reactionEndDisplacement. Cache includes source revision.
The stateless mixer now transfers terminal translation from the outgoing pose
to its anchor, compensating for actor yaw weight. Completed action offsets compose
across the timeline. Held falls keep the terminal pose without a second transfer.
Authored end orientation is not carried; current five problem kicks finish within
~0.43 degrees of their initial body heading.

Metadata writes both displacement vectors and motion_sampling v2; replay restores
them. Checkpoint hashing includes the new helper. No original FBXs or clip enabled
flags were removed or disabled. New generated scenarios receive the endpoint fix;
old videos/serialized plans retain old values. User config SHA256 unchanged.

Endpoint behaviour passed 23 focused checks: five kicks x two models x two yaw
angles plus composed action history, reaction source offset and held-fall cases.
Tail hip motion is at most 0.0917 m (stance adjustment), history composition error
1.67e-6 m, frame-order error 0. Reports: endpoint_checks.csv, endpoint_summary.json.
Compilation passed without warnings/errors.

The final pre-tail fixed-speed grid completed all 25 source attacks: 2 punch and
3 kick combinations passed; rejected contact/penetration variants remain logged
in catalog_after_root_import_before_tail_fix.json. This is not a current all-speed
acceptance claim and no safety threshold was weakened to force a clip through.

Final native examples with endpoint-aware code:
- Punch_9, seed 202401: all_motion_examples/punch9/batch_20261111_142030_901_3d9fdb,
  125 frames, contact 2.81 mm, reaction one frame after impact.
- Kick_5, seed 99454: all_motion_examples/kick5/batch_20261111_142232_144_fa9511,
  134 frames, contact 6.56 mm, reaction one frame after impact.
Both retain the named source clip and passed the exporter guards. PyAV verified
all 259 frames, FPS/PTS/resolution, visible motion and motion_sampling v2 metadata.
Actual decoded contact sheets visually inspected. all_motion_video_verification.json
records paths. No export/check running, Editor in Edit mode, ground/penetration
probe count zero. No production 100+100 batch launched; original outputs retained.

## Editor responsiveness repair — 2026-11-11

User reported Unity becoming unresponsive after manually starting the 100+100
production batch batch_20261111_143359_567_e8b4f4. unity status initially returned
STATUS_ALL_UNREACHABLE; process 99092 was Responding=false but accumulating CPU.
The other substantial Unity processes were Asset Import Workers, not other target
Editors. Seven scenarios were committed. A queued SaveAndStop succeeded, preserving
those files and restoring responsiveness in the same process, without force-close
or restart. No Unity bug report/log upload was sent.

Two concrete problems fixed: synchronous multi-variant planning monopolized the
main thread; DatasetClipDisplacement also read Animator.bodyPosition outside IK
and logged warnings. Replaced it with HumanPoseHandler.GetHumanPose and proper
avatar humanScale plus transform scale conversion. Controlled normalized/world
comparison on both avatars matched the old endpoint displacements; all 23 endpoint
tests passed, no warnings, history error ~3.34e-6 m and frame-order error zero.

Added cooperative IEnumerator steps for planning, calibration/refinement,
first-main-contact scans, per-frame penetration validation and camera preparation.
Exporter advances one step per update; pause/resume and save/stop work during
preparation. Nested iterators are disposed before preview cleanup, including error
and cancellation paths. Public synchronous APIs remain for intentional QA calls.

Initial timing tests still exposed a ~5-second BVH constructor. Cached triangle
bounds and centres during topology building instead of recomputing them inside
every sort comparison, preserving the same geometry/query criteria. Empty candidate
sets avoid unnecessary probe-tree construction. Native preparation timing now has
dedicated MaximumPreparationSliceMs/SlowestPreparationPhase diagnostics.

Final DatasetResponsivenessChecks passed planning pause, resume, mid-validation
cancellation/cleanup, and a full native 640x360/25FPS Punch_9 export. Max preparation
slice ~594 ms versus ~5,013 ms baseline; 376 Editor updates / 245 preparation updates,
zero warnings, one completed video. Five synthetic collision-volume checks passed.
PyAV decoded all 125 frames and verified PTS/FPS/dimensions; source clip, limb,
target, impact/reaction frames, speed, starts and yaw matched the previous native
seed-202401 example. Reports and historical failed timing baselines retained in
DatasetOutput/Checks/EditorResponsive_20261111/.

Backups: Backups/BeforeEditorResponsivenessFix_20261111/. Compatibility audit rebuilt
the original production source fingerprint from saved pre-fix C# bytes and current
asset dependencies, matching the original 6FBFB5... checksum exactly. Only after
behaviour tests, the specific seven-video checkpoint was upgraded to the current
source fingerprint; original resume.json retained as resume.before_responsiveness_fix.json
and editor_responsiveness_migration.txt records the scope. No generic bypass of
ValidateSources was introduced. SHA256 inventory of all 14 committed metadata/video
files matched after resumption.

Production generation resumed in the original folder from scenario 8. At the last
live check, nine videos were committed and scenario 10 was preparing; process 99092
Responding=true and CLI commands responsive. Observed production max preparation
slice ~1.26 seconds. Original user config unchanged; the native production batch
is intentionally running, and no QA check is left active. See preservation_after_resume.json,
original_input_verification.json and resumed_video_verification.json.
