# Ch01 texture experiment — 2026-11-26

Only `Assets/Characters/Ch01_nonPBR.fbx` was changed to use full-resolution
source albedo and normal maps. This is an import-resolution improvement, not
AI-generated 4K detail or a claim of photorealism. At 720p with full-body framing
the visual benefit is small; the close-up review exposes more fabric detail.
No all-character rollout, dataset render-setting change or production batch.

## Result

| Variant | PNG source size | Unity loaded size | Decision |
| --- | --- | --- | --- |
| Original albedo/normal | 4096 x 4096 | 2048 x 2048 | Retained untouched |
| Native source copies | 4096 x 4096 | 4096 x 4096 | Enabled on Ch01 body/hair |
| AI albedo v1 | 1254 x 1254 | 1024 x 1024 | Experimental only; rejected |

Active copies are in `Assets/Characters/Ch01_nonPBR_Enhanced/`:
`Ch01_Albedo_Native4K.png` and `Ch01_Normal_Native4K.png`. Their SHA-256 hashes
match their respective original source files. Import uses HQ compression,
mipmaps, trilinear filtering and anisotropy 8. The normal map is imported as a
normal map with sRGB disabled. Original source import settings are unchanged.

The AI result `Ch01_Albedo_AI_v1.png` was produced with the built-in imagegen
tool in edit mode, then copied into the project. It is not assigned to materials.
It lost all original transparency (230,281 source pixels were nonopaque) and
visibly shifted facial details around the eyes. Its output resolution is also
lower than the source. Upscaling it would not restore missing detail or UV
registration. It was therefore rejected for the dataset.

Original material files, texture import metadata and facial profiles are backed
up under `Backups/BeforeTextureUpgrade_20261126/`. Original PNG/FBX geometry,
UVs and animation files were not edited. Ch01 facial vertex anchors were kept;
only their model dependency hash was refreshed after the material change.
All 19 facial profiles still validate. No generation was running at verification.

## Review artifacts

`DatasetOutput/Checks/TextureUpgrade_20261126/comparison.png` has columns:
original 2K, native 4K, AI v1. Rows: face, clothing, full body. These are Unity
camera renders of the same mesh, pose, camera and lighting, assembled into a
smaller QA montage. They are not UI screenshots or final dataset output.
Individual 1024-square renders and variant JSON are alongside it.
`verification.json` records dimensions, alpha inspection, matching geometry
signatures and matching source hashes. `applied_verification.txt` records the
actual assigned material textures and facial profile validation.

## Exact AI edit prompt

Mode: built-in imagegen, edit. Input: the original
`Assets/Characters/Ch01_nonPBR_Textures/Ch01_1001_Diffuse.png`.

```text
Edit this existing 4096x4096 humanoid UV diffuse/albedo texture atlas in place as a high-fidelity texture refinement, for application to the EXACT SAME existing 3D mesh. This input image is the edit target, not a layout reference. Preserve ALL UV island outlines, positions, proportions, borders, edge colors and seams exactly; do not rearrange, shift, rescale, crop or rotate any element. Preserve the exact same bald adult male identity, skin tone, facial proportions, eye/ear/nose/mouth locations, blue denim jeans, white polo shirt, white sneakers, and all clothing seams/buttons. Improve only material microdetail within the original regions: natural subtle skin pores and pigmentation, believable neutral unlit skin color, very fine denim weave and stitching, cotton knit in the white polo, shoe fabric and leather grain. Make the surface material feel photographic, not painted or plastic. Keep all features precisely registered to the original image. This is a single flat square full UV color atlas, not a render or a contact sheet. No new objects, no UV wireframe, no lighting, highlights, specular reflections, cast shadows, gradients simulating 3D lighting, labels, text or watermark. Retain original atlas orientation and exact whole-image layout. Refine rather than redesign. Return at the original 4096x4096 resolution if supported; maximize real detail without inventing shapes or changing existing structure.
```
