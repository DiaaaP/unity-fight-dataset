# Randomized two-person punch test

The `FightScenarioRandomizer` component is attached to the test scene root.
Its Settings fields in the Inspector control the ranges. Parameters are chosen
once per scenario; neither the camera nor random parameters jitter per frame.

Defaults:
- Punch and reaction speed: 0.8–1.2x (same multiplier).
- Pair yaw: ±12 degrees; additional attacker yaw: ±4 degrees.
- Camera orbit around the test: ±25 degrees relative to the original view.
- Camera distance: 3.8–4.5 m; height: 2.3–3.1 m; FOV: 38–48 degrees.
- Extra camera aim yaw/pitch: ±3 degrees.

Play picks a fresh seed by default. Turn off New Seed On Play and set Seed for
reproducible previews; turn off Randomize On Play for the unmodified saved scene.
The saved baseline prevents cumulative rotations/position drift.

Use Tools > Fight Dataset > Export Randomized Test MP4 for a fresh random export,
or Export Fixed Test MP4 for the saved unrandomized layout.
From Unity CLI: `unity command script eval "return FightTestVideoExporter.StartExport(12345);"`
repeats the scenario for seed 12345 with the current configuration and assets.

JSON stores the seed, actual camera/actor transforms, sampled parameters and
configured ranges, attack/reaction speeds, and adjusted event times. The receiver
is horizontally repositioned against the sampled fist at impact. This is an
approximate geometric alignment, not a physics/contact guarantee. Angle variation
rotates actors, not individual joints. Additional bone perturbations are not used.

Camera candidates must contain padded bone samples at key action times. Export
also checks the major body landmarks on every frame and fails if they leave the
image. This does not guarantee absence of self-occlusion or full geometric realism.
The exporter retains explicit CPU mesh baking to avoid the previous GPU skinning
desynchronization during Edit-mode rendering. Outputs remain in DatasetOutput.

Crossfades use quintic smootherstep easing with separate wall-clock durations:
attack entry 0.28 s, reaction entry 0.22 s, and return to Idle 0.4 s. After an action
finishes its final pose is held during the return fade, while Idle keeps running.
These values are fields on FightAnimationTest. Metadata records crossfade settings
and separate action-end and full-return-to-Idle timestamps.

`FightRandomizationChecks.Validate()` checks repeatability, speed/timing variation,
camera stability, and 1,200 sampled frames over 12 seeds without generating videos.
