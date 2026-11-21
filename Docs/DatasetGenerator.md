# Unity synthetic interaction dataset generator

## Upload assets

Drop humanoid FBX files into these folders:

- `Assets/Characters`: character models with skin and textures.
- `Assets/Animations/Attacks/Punch`: punches, any filenames.
- `Assets/Animations/Attacks/Kick`: kicks, any filenames.
- `Assets/Animations/Neutral`: Idle and optional normal motions.
- `Assets/Animations/Reactions`: hit reactions; optional Block, Dodge, Stumble,
  Fall and GetUp subfolders are created automatically.
- `Assets/Animations/Inbox`: automatically classify recognizable names such as
  Punch, Jab, Uppercut, Kick, Idle, HitReaction, Block, Dodge and Stumble.

Unity imports these FBX files as Humanoid, bakes root movement into the animation
pose, and refreshes the catalog after import. Use
`Tools > Fight Dataset > Generator > 1 Refresh Animation Catalog` to refresh
explicitly. Unknown names remain Unknown and are not silently used as attacks.
There is no content-based neural animation classifier: folder names and filename
rules provide automatic routing, while trajectory sampling estimates attack timing.

Open `Assets/Config/AnimationCatalog.asset` to inspect the results. Entries have
stable asset/subasset keys, enabled state, kind, speed range, optional impact time
and attacking limb overrides. Enable Manual Classification to preserve a chosen
kind through refreshes; enable Override Impact to use your impact time and limb.
Preferred Reaction Key can select a specific compatible reaction, and Override
Target Landmark chooses the anatomical calibration target explicitly.
New files need no Animator states or manually connected transitions. Deleting an
asset removes it from the next catalog refresh. Invalid Humanoid clips and missing
categories are reported rather than replaced by invented assets.

## Configure and run

1. Use `Generator > Select Generator Config` to select
   `Assets/Config/DatasetGeneratorConfig.asset` in the Inspector.
2. Configure seed, scenario count, output directory, dimensions, FPS, minimum
   duration, category toggles, view count (1–3), fight length and transition times.
3. `Generator > 2 Build Or Open Generator Room` opens a separate saved preview scene.
4. `Generator > Validate Generator Without Rendering` performs deterministic checks.
5. `Generator > Run Small Smoke Test` renders one scenario per currently available
   category, two camera views each. `3 Generate Configured Batch` uses the full config.
6. `Generator > 0 Generation Progress` opens the dashboard (also opens on start).
   It shows overall progress, the current frame, completed videos, failures and
   active elapsed time, with Pause, Resume and Save And Stop controls.

Equivalent terminal commands from this project:

```powershell
unity command script eval "DatasetCatalogTools.Refresh(); return true;"
unity command script eval "return DatasetGeneratorChecks.Validate();" -- --timeout 30000
unity command script eval "return DatasetBatchExporter.StartSmokeTest();"
unity command script eval "return new { DatasetBatchExporter.Status, DatasetBatchExporter.CompletedScenarios, DatasetBatchExporter.FailedScenarios };"
unity command script eval "return DatasetBatchExporter.StartBatch();"
unity command script eval "DatasetBatchExporter.Cancel(); return DatasetBatchExporter.Status;"
unity command script eval "DatasetBatchExporter.Pause(); return DatasetBatchExporter.IsPaused;"
unity command script eval "DatasetBatchExporter.Resume(); return DatasetBatchExporter.Status;"
unity command script eval "DatasetBatchExporter.SaveAndStop(); return DatasetBatchExporter.Status;"
unity command script eval 'return DatasetBatchExporter.ResumeBatch("C:/absolute/path/to/batch");'
```

Generation is an autonomous Editor update job. One command starts the local batch;
no AI calls or terminal requests are made per video frame. Keep the Editor open and
in Edit mode until completion. Pause keeps the live encoders and exact frame in
memory. Save And Stop, entering Play, script reload, editor quit or catalog refresh
suspend the batch and release encoders. Resume after a restart uses the same output
directory and frozen configuration/catalog settings. Completed scenarios are never
encoded again; the unfinished scenario restarts at frame zero because a closed MP4
encoder cannot safely append frames. Uncommitted outputs are archived with an
`.interrupted` suffix in logs/interrupted, outside the usable MP4 folders.

`resume.json` stores a portable configuration and catalog snapshot with stable
asset/subasset references and a fingerprint of the source models, animations,
room materials, generator code and Unity version. `manifest.json` is the atomic
commit record and stores next_scenario_index; a scenario is committed only after
all views and metadata are finalized. Recovery rejects changed sources, missing
completed files, inconsistent metadata and completed batches before writing.
No automatic restart occurs on opening Unity; choose Resume in the dashboard.
Older batches without resume.json cannot be resumed with this mechanism.

## Asynchronous frame capture

Use Async GPU Readback defaults on. Supported GPUs use a bounded FIFO of three
scenario frames, with a dedicated render target and persistent RGBA32 buffer per
view/slot. Pose sampling and CPU skinning remain shared across all camera views.
Projection lines and inspection-frame decisions are captured with their source
pose; encoders receive completed frames in exact frame/view order on Editor update.
The last frame is drained before finalizing MP4s and committing the scenario.

Request callbacks only retain completion/error flags, because native request
handles expire after completion. Edit-mode capture polls requests with Update()
and disables forcePlayerLoopUpdate to avoid running the entire Player loop for
each request. Native buffers go directly to MediaEncoder.AddFrame; inspection PNGs
are encoded from the CPU copy without uploading it back to the GPU.

Pause retains the bounded queue without advancing encoded-frame progress. Stop,
script reload and shutdown wait for outstanding native writes before releasing
buffers/targets. Unsupported GPUs use the synchronous path. A GPU readback error
rejects the current uncommitted scenario and records a fallback reason in the
manifest; subsequent scenarios use synchronous capture. The config option can
explicitly select that path for comparison/troubleshooting.

The exporter signals the next Editor tick while actively generating, and stops
doing so on pause/finish. This prevents background Editor throttling from turning
GPU queue polling into long idle gaps. It does not change the project's FPS,
resolution or saved Run In Background setting.

Controlled RTX 4060 Laptop benchmark: eight identical hit/kick scenarios, two
views, 1280x720 at 20 FPS, inspection PNGs enabled. With identical Editor wake-up
behavior, synchronous capture took 14.178 s and async capture 10.691 s for 16
videos (24.6% less time, 1.33x throughput). All 1,007 pose signatures/timestamps
and 64 inspection PNGs matched. The original background-throttled run took
41.374 s; comparing only that number overstates the async-only gain.
Report: `DatasetOutput/Checks/async_readback_benchmark.json`.
Independent full-frame MP4 verification is recorded in
`DatasetOutput/Checks/async_readback_decode.json`: all 16 benchmark videos plus
six pause/recovery videos decoded with correct dimensions, FPS, frame counts and
timestamps. Most decoded benchmark frames were RGB-identical to the control;
19/2,014 had tiny differences, worst PSNR above 68 dB. Unity's separate VideoPlayer
checker timed out on startup twice, so its report remains failed rather than
being replaced with the independent result.

## Implemented behavior

`Scenarios Per Category` (serialized field `scenariosPerCategory`, with automatic
migration from `scenarioCount` and a compatible C# property) is the target per
enabled, available category. With Hit/Kick enabled, 100 means 100 hit + 100 kick
scenarios, 200 total. Camera Views multiplies the MP4 count. Smoke tests still
request one scenario per available category, two views each. The optional CLI/API
`scenarioLimit` remains an overall cap for diagnostic runs. Manifests store
`count_mode=per_category`, `scenarios_per_category`, `total_scenario_limit` and the
combined `requested_scenarios`; progress and resume use the combined total.

- Floor, four walls and a ceiling for low-angle shots; key/fill lights.
- Two randomly selected Humanoid models (a single available model can be cloned).
  Actor height and ground alignment are normalized without editing the FBX files.
- Least-used-compatible attack selection with randomized ties, 0.8–1.2x speed by default, orientation and safe center
  variation. Calibration samples the actual blended pose at output-frame times,
  fits initial actor spacing, and verifies weighted hand/foot mesh vertices against
  the selected anatomical region's baked triangles. Arms held in front of the
  body cannot count as head/torso hits. The dominant striking limb must be moving
  toward the target; idle/recovery touches are rejected. Default maximum surface gap is 8 mm. Later fight
  attacks must contact from the same stance; incompatible attacks are reselected.
  Contact is verified again at the actual encoded impact frame. Unverified contact
  is rejected even if an old config disabled the legacy rejection toggle.
- Source-space outgoing-motion profiles identify the main stroke before contact
  placement. Palm/proximal-knuckle patches replace fingertip-only hand tests;
  both hands and feet use the leading surface in their actual incoming direction,
  so a trailing edge cannot stand in for the striking surface.
  Runtime attack yaw aligns the source stroke with the opponent, smoothly weighted
  with the attack. Candidate impacts must occur near main-stroke extension, after
  blending in; incidental preparation contacts do not trigger reactions. A later
  significant outward burst can trim the source playback range. Original FBXs are
  not rewritten. Stance spacing supports short hooks down to 0.45 m; the encoded
  frame torso-overlap and room-bound checks remain in force.
- Hit/kick/fight select contact reactions, preferring head/body filename matches.
  Block/Dodge are excluded from these contact-only scenarios. Reaction weight is
  zero at the impact frame; visible head/torso movement must follow within one or
  two output frames. Per-action shorter blending and leading-source offsets are
  calibrated on the actual blended pose against a no-reaction baseline. This is
  verified before export and again at the encoded impact. Source clips should contain a single strike.
  Falls require
  Reviewed Fall in the catalog; they are allowed only as the last reaction, remain
  on the floor through the end, and never blend directly back to standing Idle.
  Unreviewed falls and GetUp remain excluded. Fall names take precedence over a
  Stumble folder. Refresh repairs existing fall FBX root-height import settings.
  All retargeted poses receive visible-mesh floor-penetration correction. It only
  lifts geometry that would enter the floor; intentional jumps are not pinned down.
- Fight exchanges alternate actors after attack/reaction recovery, preventing
  overlapping incompatible states. Fight duration extends to include all events;
  it may be longer than the configured minimum duration.
- Normal scenarios use available neutral clips. With the currently supplied Idle
  only, negatives are standing Idle scenes, not an extensive hard-negative library.
- Smooth Playables blending: complete action, hold final pose while fading to a
  continuously running neutral clip. Speeds are per action, not global Animator speed.
  Independent neutral phases/tempos, randomized lead-in/recovery gaps, transition
  times and reaction speeds reduce synchronized/stiff timing. Standing reactions
  are preferred; reviewed falls have a 15% selection chance when both are available.
- 1–3 independent static views with continuous azimuth/distance/zoom variation
  across six families: eye_level, low_angle, high_angle, overhead, over_shoulder,
  wide_room. Default heights span 0.25–3.35 m and FOV 35–90 degrees; wide/medium/
  close_full_body framing varies occupancy. Views are separated by position and
  azimuth/elevation. Whole-body framing is checked over the animation envelope;
  double-sided segment/mesh tests reject contacts hidden behind either actor.
  The last 7 cm near the contact surface are allowed for hand/foot thickness;
  this is a geometric view guard, not pixel-perfect/alpha-aware occlusion labels.
  Default relative weights are high 45,
  overhead 15, wide 15, eye-level 15, shoulder 5, low 5: 75% upper cameras in a large
  sample. Each view draws independently from the weights; a family can recur at a
  different position. Source/category ordering does not select the camera family.
  Render skinning is shared across views; grounding/contact probes also bake meshes.
  The pose is rendered into
  every view before time advances. There are no camera cuts inside an output clip.
- Camera framing checks, wall/floor geometry checks, contact-error threshold and
  deterministic retry/failed-scenario reporting. Authored root translation and
  rotation are retained in the sampled skeleton; poses are evaluated at absolute
  source time, without accumulating root-motion deltas between frames.

Metadata includes impact_frame, mesh_contact_verified, attack_contact_point and
target_contact_point, approach_speed_mps, per-actor neutral phase/speed and
animation_selection trials/rejection reasons, plus camera family, framing, height, distance, azimuth,
elevation and occupancy. This is geometric surface-contact validation, not a
physics simulation. Old videos retain their old contact/camera behavior and must
be regenerated to use the new logic. Checkpoint fingerprints intentionally reject
continuing a legacy partial batch with changed generator code.

Contact schema v3 additionally records source stroke start/end/peak, attack yaw
and fade, source playback end, ignored preparation-contact frames, reaction source
offset/fade and measured visible response frame. The previous v2 geometric test
could pass early preparation or late recovery touches; its numerical passes do
not establish correct strike/reaction timing. Revision regression cases are the
six user-reported videos from batch_20261109_165453_010_920226, with source clips,
speed, seed, models and original reaction clips retained during validation.

Animation balance counts committed actions, not attempted clips or camera copies.
Underused clips receive up to three speed trials before fallback. Failed variants
remain in the accepted scenario's selection log. Manifest animation_usage and the
final animation_usage.csv expose completed/attempted/rejected counts for every
enabled attack. Resume rebuilds counts from committed scenario metadata. Uniform
coverage of incompatible clips is deliberately not promised.

Temporary preview scenes explicitly reset their players before closing. Scene-close,
script-reload and Editor-quit handlers also release owned playback. Graphs are
stopped and their outputs/playable subgraphs released before destruction;
[Unity defers native graph destruction](https://docs.unity.com/en-us/engine/6000.0/script-reference/unityengine/playables/playablegraph/destroy).
The resource regression checks repeated scene creation/closure for retained
playable nodes and removes unowned transient probe meshes from earlier runs.

## Outputs

Each run creates a unique directory under the configured output root, outside Assets:

```text
DatasetOutput/Generated/batch_<timestamp>_<id>/
  hit/                  # separate *_cam01.mp4, *_cam02.mp4, ...
  kick/
  fight/
  normal/
  metadata/             # one JSON per underlying scenario
  inspection/           # selected original rendered frames, if enabled
  joint_projections/    # optional raw per-view rig projections
  yolo/                 # when Export Yolo Pose is enabled
    data.yaml
    images/train/       # also val/ and test/
    labels/train/       # same stems, .txt instead of .jpg
  config.json
  resume.json           # saved settings and source fingerprint for recovery
  manifest.json
```

Metadata includes model/clip asset paths, seed, actual actor/camera transforms,
speeds, planned impact and reaction intervals, smooth transition durations,
frame timestamps, pose signatures, category, event class, planned outcome,
quality flags and split/group identity. Planned contact/block/dodge is distinguished
from physical verification. No successful hit is asserted solely from a folder name.
For a held fall, `holds_fallen_pose=true` and `idle_return_seconds=-1` indicate
that the actor stays down for the remaining scenario duration.

Split groups conservatively combine character identities and the set of source
attack/neutral clips. All views and seed variants of that family go to the same
train/val/test split. With only one attack and two models there may be very few
groups, so the resulting split proportions need not match the requested fractions.
Add diverse animations/characters before measuring ML performance.

`manifest.json` reports requested/available/skipped categories, completed scenarios
and videos, failures and missing assets. A completed encoder job is not a guarantee
of visual or physical realism.

## Verification and optional projections

Latest strike/reaction review, finalized 2026-11-11:
`DatasetOutput/Checks/StrikeTiming_20261110/review_ready/` contains the six exact
user-reported cases under their original filenames, comparison.csv, decoded
contact sheets and verification.json. All 803 MP4 frames decoded; main-strike
contact and visible response within 1–2 output frames were checked in Unity.
Final contact/camera regression completed all 131 checks (67 source clip/speed
combinations accepted, eight rejected, 23 distinct accepted source clips).
Final pause/domain-reload recovery also passed, including six decoded recovery
videos. Original production batches are retained for comparison and need fresh
generation to obtain the new timing behavior.

Current weighted-camera review (2026-11-09):
`DatasetOutput/WeightedCameraReview_20261109/batch_20261109_163736_476_8e98dc/`.
All 40 requested videos completed without scenario failures; the independent
decoder verified 5,596 frames. There are 20 distinct accepted attack clips with
usage differing by at most one within each class (seven punches used 2–3 times,
thirteen kicks used 1–2 times). All six camera families occur, including three
low-angle views and 27 upper views. See its `animation_usage.csv`,
`verification.json`, and `DatasetOutput/Checks/weighted_camera_video_verification.json`.
The current contact/camera regression passed 131 cases; 54/75 individual source
clip/speed trials were accepted and 21 rejected. Five clips had no accepted
variant in this review; they remain enabled in the catalog for future trials,
and failures are logged. A small batch does not guarantee the configured camera
proportions exactly.

The 2026-11-07 contact/camera review is in
`DatasetOutput/ContactCameraReview_20261107/contact_camera_review_ready/`.
Its independent PyAV/FFmpeg check decoded all 27 MP4s / 3,228 frames and checked
dimensions, FPS, every presentation timestamp and contact metadata. Unity checked
the actual mesh contact before and during encoding. See
`contact_camera_review_verification.json` for that historical video report;
`DatasetOutput/Checks/contact_camera_checks.json` now describes the current version.
Camera families are selected from a hashed seed to avoid correlating category
ordering with camera height. These are continuous camera samples, not six fixed
camera positions.

The legacy Unity VideoPlayer verifier can be invoked in Play mode:

```powershell
unity command script eval 'DatasetBatchVerifier.Start("C:/absolute/path/to/batch"); return DatasetBatchVerifier.Status;'
```

This reads every MP4 frame, checks dimensions/FPS/duration against metadata,
frame timestamps, split grouping, nonempty content and movement for attack clips.
It writes `verification.json` and selected frames decoded from the actual MP4 into
`decoded_inspection`. It does not check every frame's contact quality automatically.
Player lifecycle operations are deferred outside native video callbacks; the crash
and the subsequent repeat verification are recorded in `Docs/VideoVerification.md`.
It also timed out in later checks; the independent decoder was used for the review
above. Do not treat a timeout or an uncompleted verification.json as a pass.

Optional Export Projected Body Joints emits normalized top-left 2D rig projections
and projected body bounds, using a named 17-slot mapping. Unavailable face landmarks
remain unavailable. Occlusion is explicitly unchecked, visibility is unknown, and
the files have `training_ready=false`. They are not silently converted to YOLO labels.

YOLO Pose export is now implemented as described below. Person tracking, temporal
classification and ML training/evaluation remain the separate later ML stage
from the original brief. No weights/datasets are downloaded and no training
is run by this generator. Motion capture clips can still need manual impact and
reaction review; automatic calibration is an estimate, not a substitute for review.

## YOLO Pose / COCO 17 — 2026-11-18

В `Generator > Select Generator Config` включён **Export Yolo Pose**. При обычной
генерации вместе с MP4 сохраняется каждый кадр JPG и одноимённый TXT. Кнопка
`3 Generate Configured Batch` остаётся прежней. **Pose Jpeg Quality** управляет
качеством JPG (по умолчанию 90). Дополнительное сохранение кадров требует места
на диске и времени на кодирование; эти затраты не входят в старые MP4-бенчмарки.

Пример внутри нового батча:

```text
yolo/images/train/hit_00001_s202401_cam01_00000.jpg
yolo/labels/train/hit_00001_s202401_cam01_00000.txt
yolo/data.yaml
```

Одна строка TXT — один человек, обычно две строки. Класс всегда `0: person`;
hit/kick остаются категориями сценария в JSON, а не классами людей.
Строка содержит 56 чисел: класс, центр/ширина/высота рамки и 17 троек `x y v`.
Координаты нормализованы в [0,1], начало сверху слева. Порядок точек:
нос, левый/правый глаз, левое/правое ухо, плечи, локти, запястья, тазобедренные
суставы, колени, голеностопы; стороны анатомические, относительно персонажа.

- `v=0`: точка вне кадра/диапазона глубины, записывается `0.0 0.0 0`.
- `v=1`: точка в кадре, но перекрыта; сохраняются реальные проекции и `1`.
- `v=2`: точка видна по геометрической проверке.

Проверка лучей использует те же деформированные треугольники, что рендерятся
в видео: другой персонаж, собственные конечности, стены и пол могут закрывать
точку. Учитываются отсечение обратных граней и alpha-cutout материала; для
полупрозрачных поверхностей применяется порог alpha 0.5. Непрозрачные материалы
остаются непрозрачными, даже если их текстура содержит неиспользуемый alpha.
Физические коллайдеры для этой проверки не нужны.

Суставы находятся внутри кожи, поэтому ближайшая поверхность собственной
анатомической области допускается в пределах её толщины (8.5–19 см в зависимости
от сустава). Другие области тела считаются перекрытием. Это геометрическая
аппроксимация видимости анатомических суставов, не ручная COCO-разметка.
Нос/уши привязаны к вершинам головы; глаза используют поверхность глаз возле
глазных костей, если она есть, иначе подбираются по геометрии лица. Оба текущих
персонажа проверены наложением точек спереди/сзади/сбоку/сверху. **При добавлении
других моделей нужно проверить подбор лица и толщины областей на малом батче.**
Использованные mesh/vertex-привязки сохраняются в `pose_face_anchors` метаданных.
Рамка вычисляется по проекциям вершин текущей позы, а не по статической T-pose.
Штатный генератор сохраняет полное тело в кадре; произвольное пересечение
персонажа ближней плоскостью камеры не является поддержанным режимом рамки.

Изображение, TXT, номер кадра и ракурс связаны до продвижения времени анимации.
Асинхронная GPU-очередь хранит соответствующую ей разметку, очередь фонового JPG
ограничена шестью изображениями. Частичные пары лежат в `yolo/.pending`;
в train/val/test они переходят только при завершении сценария. Manifest остаётся
записью о завершении. При возобновлении незаконченные пары и частично перенесённые
файлы архивируются в logs/interrupted. Не используй файлы вне завершённых
сценариев manifest во время активной генерации.

Все кадры/ракурсы одного семейства попадают в один split; YAML содержит
`kpt_shape: [17,3]`, зеркальный `flip_idx` COCO и абсолютный путь к yolo-папке.
После переноса батча исправь `path` в data.yaml. В маленьком тесте val/test могут
оказаться пустыми: не дели соседние кадры одного ролика между train и val.
В стандартном pose loss Ultralytics точки с v=1 также размечены: флаги 1/2
не означают разные классы действий.

Проверка реализации: `DatasetOutput/Checks/YoloPose_20261118/verification.json`.
Сравниваются все JPG с независимо декодированными кадрами MP4, формат каждой
строки, координаты, видимость, асинхронная/обычная запись и остановка/продолжение.
Наложения точек и рамок лежат рядом. Старые MP4 автоматически не размечаются:
нужен новый батч, чтобы получить точные синхронные позы.

Спецификация формата: https://docs.ultralytics.com/datasets/pose/
Потокобезопасное кодирование JPG: https://docs.unity.com/en-us/engine/6000.0/script-reference/unityengine/imageconversion/encodearraytojpg

### Inter-actor penetration guard (2026-11-11)

`DatasetPenetrationGuard` skins both characters with the same BakeMesh(true)
coordinate convention as the exporter. Fixed-topology triangle BVHs are refitted
per pose. Up to roughly 1,800 vertices and 600 face centroids per skin are checked
against the other character in both directions. A violation requires nearest
surface depth and inward normal depth greater than 20 mm plus an inside majority
from three non-axis-aligned ray parity tests. The normal check avoids treating
points outside open clothing boundaries as deeply inside merely from ray parity.
This is sampled geometric validation, not exact solid collision or physical response;
open/non-manifold clothing and unsampled small intersections remain limitations.

The planner checks all output frames through preparation, impact, reaction,
blend-out and idle return before accepting an animation variant. A coarse pass
rejects clear failures early; accepted plans then check every remaining frame.
The exporter repeats the guard at every submitted frame and disposes its probe
meshes on scenario cleanup. Failure reasons remain in animation_selection; source
catalog assets are not silently disabled. Metadata adds penetration_validation =
`bidirectional_sampled_mesh_inside_depth_20mm_every_output_frame_v1`.

Contact calibration approaches from a separated stance instead of accepting the
first unsigned near-zero distance, which can also describe an already embedded
hand/foot. Impact placements with deep penetration are rejected as well.
The guard source participates in checkpoint fingerprinting. Existing videos are
unchanged, and a checkpoint from earlier generator code requires a new batch.

### Authored root motion on existing FBXs (2026-11-11)

DatasetModelImporter already configured XYZ translation and rotation to bake into
the pose, with original offsets retained. However, old FBXs with empty clipAnimations
had never received that postprocessor policy; Refresh previously repaired only
falls. Their root displacement/orientation was extracted while manual sampling
disabled applyRootMotion. Punch_7 consequently lost almost all of its lunge and
the strike profile compensated with an artificial 77 degree actor turn.

Refresh now ensures this existing policy for every animation FBX, including old
imports and future additions. Idle/Neutral looping and fall height handling remain
consistent with the postprocessor. All 41 clip keys were preserved after reimport.
The source-profile cache key now includes the asset dependency hash, so import
changes cannot reuse an old trajectory. Stance checks use actual posed hip centres
rather than stationary scene sampling anchors.

Comparison on both settings versions of all 25 enabled attack clips passed finite
pose / bone-length checks. Punch_7 horizontal hip travel restored from 0.0472 m to
0.7083 m. Its maximum foot span is about 1.19 m in both versions: the wide stance is
authored in the source clip, not entirely an artifact of lost root motion. Foot IK
was inspected separately and left unchanged; there is no new world-space foot lock.
Exact-frame replay after sampling the end and start produced zero world-position
difference on 106 bones, with impact and reaction revalidated.

Native exports of the same Punch_7 and Kick_12 source clips completed with full
penetration/floor/framing guards. Independent decoding passed all 254 frames,
timestamps, dimensions/FPS and visible motion; contacts 2.83/7.48 mm and reactions
one output frame later. Reports and decoded contact sheets:
DatasetOutput/Checks/RootMotion_20261111/. Earlier contact/camera and acceptance-rate
reports describe the previous import state, not a fresh full 131-case regression.

### Terminal displacement and idle handoff (2026-11-11)

A complete audit checked all 41 clips on both models: 82 clip/model pairs and
5,002 source poses, with no invalid coordinates or anomalous bone-length ranges.
The largest segment-length variation was the configured Humanoid stretch (~5%).
Comparing saved pre-migration FBX metadata confirmed that 36 imports needed repair
and five already followed the policy. See all_animation_fixes.csv and
all_animation_audit_summary.json in the RootMotion check folder.

Five kicks have substantial net displacement at their end: kick_6, kick_7, kick_9,
kick_15 and kick_17. Blending their baked pose back into the initial idle anchor
previously pulled the character backwards by 0.5–2 m. DatasetClipDisplacement now
samples canonical Body Transform endpoints in metres, caches by avatar/clip/scale,
source end and asset revision, and supplies horizontal displacement to the plan.
It respects trimmed attack source ends; reactions retain the full authored endpoint.

During blend-out, DatasetScenarioPlayer transfers the part of terminal translation
that is disappearing from the pose onto the sampling anchor. The transfer accounts
for the attack yaw blend. Completed actions contribute their displacement to all
subsequent samples; positions are recomputed from the timeline each time, rather
than integrated. Falls which hold their final pose do not apply the transfer again.
This is horizontal position carry; authored end orientation still blends to idle.

Action metadata includes attack_end_displacement and reaction_end_displacement;
motion_sampling is authored_pose_with_absolute_horizontal_endpoint_carry_v2.
Metadata replay restores these vectors, and the displacement helper participates
in checkpoint source fingerprints. Existing serialized plans/videos lack these
new endpoint values and need a newly generated scenario to receive the change.

Endpoint regression passed 23 cases: all five kicks on both models at 0/60 degree
yaw, two-action history composition, frame order, offset reaction playback and a
held fall. Residual hip motion during stance change was at most 9.2 cm, instead of
the old 0.5–2 m return; history position error was 1.7 micrometres and repeated
frame error zero. Reports: endpoint_checks.csv and endpoint_summary.json.

The fixed-speed contact grid after import migration tried all 25 attacks: 2 punch
and 3 kick combinations accepted, others rejected with reasons. This grid preceded
the endpoint handoff correction, is not a clip blacklist and does not mean every
seed/reaction pairing is valid. The final native Punch_9 and Kick_5 exports passed
all guards; independent decoding verified 259 frames, motion, FPS/PTS, source clip
identities, new motion metadata and one-frame reaction onset. Results:
all_motion_video_verification.json and all_motion_examples/ in the check folder.

### Editor responsiveness during preparation (2026-11-11)

Scenario preparation now runs as cooperative Editor-update steps: contact sampling
and refinement, first-contact scanning, whole-timeline penetration validation and
camera envelope/selection all yield between work units. Synchronous wrappers remain
for deliberate command-line checks. The exporter shows the current preparation
phase and can pause, resume or save/stop before encoding has begun. Cancelling
disposes nested iterators before closing the temporary scene and probe meshes.

BVH construction caches initial triangle bounds/centres before sorting; it no
longer recomputes them millions of times inside comparison delegates. Dynamic
pose refitting and collision thresholds are unchanged. Empty contact-candidate
sets do not allocate an unused penetration tree.

Endpoint motion uses HumanPoseHandler.GetHumanPose instead of Animator.bodyPosition
outside IK callbacks. Avatar normalization and root transform scale are converted
back to world metres. The supported API passed all 23 endpoint checks with zero
warnings. A native pause/cancel/restart/export test passed, as did five known-volume
collision cases and independent decoding of 125 test frames. Strike identity,
timing, speed and reaction matched the earlier seed-202401 source test. The slowest
preparation step in that test fell from ~5.01 seconds to ~0.594 seconds. Wall-clock
Editor-update observations also include CLI C# compilation, so dedicated preparation
timers are used to attribute generator work. Reports: EditorResponsive_20261111/.

The production batch batch_20261111_143359_567_e8b4f4 was safely suspended at seven
committed videos, without restarting Unity. For this specific compatible code fix,
its original source fingerprint was independently recomputed using saved pre-fix
C# bytes and current asset/config dependencies; it matched exactly. After behaviour
tests, a backed-up, documented checkpoint upgrade allowed resuming from index 7.
Normal source-fingerprint validation remains strict. All 14 committed metadata/MP4
files matched their pre-upgrade SHA256. See the migration note and original resume
backup in the production batch; no generic mismatch bypass was added.
