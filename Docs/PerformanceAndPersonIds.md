# Person IDs, material repair and generation speed — 2026-11-26

## Current throughput on this workstation

Two batches of five Hit and five Kick videos took 58.61 and 58.17 seconds.
The previous implementation took 72.35 and 67.88 seconds on the same seeds,
models, actions and frame counts. The combined saving was 16.7%. Both batches
used one view at 1280x720 and 25 FPS with a JPEG and COCO17 label for every
frame. At the measured mean of 5.84 seconds per video, 10,000 videos would take
16.2 hours and 20,000 would take 32.4 hours. Full-scale disk and indexing costs
have yet to be measured.

The exporter reuses bounded RGBA buffers for JPEG encoding, updates mesh trees
on demand, and carries verified joint positions and body bounds into camera
selection. Camera angle fitting runs in Burst. Each trial reaction is compared
with neutral poses sampled at the same two frame times.

Burst computes the floor minimum, nearest contact and skinned world-space
vertices. Contact attempts reuse a scenario's intersection tree. YOLO computes
head membership once per bone and projects possible bounding-box extrema with
the Unity camera. Partial views check the posed body bounds during planning,
allowing another view to be drawn before recording starts. Fresh scenario slots
skip the interrupted-file scan. Resume and failure cleanup still archive
partial output.

Independent decoding verified 2,666 frames across 20 videos. All COCO visibility
flags agreed with the previous export. The largest normalized coordinate
difference was 0.000007. Both batches selected 9 inter-person occlusion frames
out of 180 training frames. The timing, geometry comparisons and paths to the
exports are in DatasetOutput/Checks/Throughput2_20261126/results.json.

## Identity metadata

Each actor now has two numeric identifiers:

- `track_id`: 1 or 2 within a scenario. Identifies the actor instance even if a
  single-model test uses the same model twice.
- `person_id`: persistent identity of the model across scenarios, roles,
  heights, cameras and batches.

For example, Ch01 is person 1, Ch08 is person 4. Either can appear as track 1
or track 2. The existing `id: actor_A/actor_B` and action references remain
compatible. Actions additionally carry `actor_person_id` and `target_person_id`.
YOLO's person class remains 0; these IDs are metadata, not extra YOLO classes.

The append-only registry is `Assets/Config/DatasetPersonIds.json`. GUIDs preserve
IDs when a model is renamed or moved. New models get new numbers; existing
numbers are not renumbered when a new model is added. The identical Ch42
downloads share an identity. Current registry: 19 model files / 18 people.
Each batch includes a `people.json` snapshot.

All ten metadata files in `DatasetOutput/Generated/2` were updated. Original
JSON files are under `Backups/BeforeOptimization_20261126/metadata_2`.
`identity_verification.json` confirms all previous metadata fields were
preserved and all actor/action IDs match the registry.

## Material repair

Atlas numbers were previously guessed from body/hair material names. Ch08
actually uses 1001 for skin, 1002 for clothing and 1003 for hair. The wrong
assignments covered its clothes and hair in skin/clothing fragments. Ch06's
second body material had a similar problem.

`DatasetFbxMaterials` reads the binary FBX material-to-texture connections,
without modifying the FBX or its geometry. All 19 files were checked: 10
diffuse/normal slots corrected, including source normal-map connections on
other models. Facial anchors were preserved and dependency signatures refreshed
only for affected models. Material/landmark backups are alongside the code
backup. Preparing a new model now uses its actual FBX diffuse connection; it
refuses to guess between multiple unresolved atlases.

Review render: `DatasetOutput/Checks/Optimization_20261126/Ch08_fixed.png`.
Existing MP4/JPG pixels in folder `2` retain their old appearance. Future renders
use the repaired materials.

## Performance changes

- Burst refits the triangle BVH and runs penetration point queries on CPU worker
  threads. Unity object access and mesh baking remain on the Editor thread.
- The reference nearest-triangle, oriented-normal and three-ray parity checks
  remain available for regression comparison. The 20mm rejection threshold and
  point/triangle-centroid sample set are retained. Ray-hit buffer overflow falls
  back to the managed query instead of truncating hits.
- YOLO reuses the Burst BVH refit. Interior box points are pruned with a coarse
  matrix projection; potential extrema use Unity's original projection. Joint
  projection and geometric visibility tests remain unchanged.
- Alpha pixels are cached by texture instance, size and asset dependency hash,
  with a 128MiB pixel cache limit. Mutable transient textures are not cached.
- Rendering and penetration checks share the frame's baked mesh, avoiding a
  second BakeMesh call. Native buffers are disposed on scenario cleanup, including
  failed attempts; YOLO review helpers also dispose their buffers.
- Plan validation first checks contact, follow-through and return poses, then
  coarse samples and all remaining frames. Accepted plans still check every
  output frame exactly once. This changes rejection order, not the coverage.

New scenario metadata includes `generation_timing` with `preparation_seconds`,
`frame_export_seconds` and `finalization_seconds`. These wall times include
Editor scheduling; they are not CPU profiling percentages.

No output resolution/FPS reduction, frame-label subsampling, relaxed contact
threshold, or disabled penetration check was used to obtain the speed results.
The saved production config remains 100 Hit + 100 Kick, one camera, 720p/25FPS.

## Validation and timings

Artifacts are in `DatasetOutput/Checks/Optimization_20261126`:

- `differential.json`: 1,362 original frames plus 30 deliberately overlapping
  placements, all 10 scenarios. Penetration acceptance/rejection and maximum
  depths match the managed reference within the recorded floating-point error.
  One heavily overlapping placement differs by one diagnostic sample count;
  rejection and maximum depth agree. Sample counts are not the pass criterion.
- `yolo_differential.json`: 50 before/contact/after/endpoint frames across all
  ten scenarios; normalized coordinates and visibility flags match exactly.
  This probe measured about 4.6x faster YOLO geometry/label work.
- Existing synthetic volume and mutable-alpha cutout tests passed.
- `benchmark_directory.txt`: final ten-video benchmark with the same seed,
  16-model pool, five Hit/five Kick, 1280x720, 25FPS, one view and all-frame YOLO
  output as folder `2`. `first_benchmark_directory.txt` preserves the intermediate
  run before the critical-frame ordering improvement.
- The original batch took 595.38 seconds; the intermediate Burst run took
  184.84 seconds (3.22x faster). Final results and independent MP4/label checks
  are recorded in `speed_summary.json` and `export_verification.json`.

These are local test results on ten scenarios, not a guaranteed rate for every
animation/model pair. Any 10k/20k projection is an extrapolation from the measured
batch, not a completed bulk run. New code/material dependencies intentionally
invalidate resume fingerprints for older unfinished batches; completed outputs
and their metadata backups remain available.
