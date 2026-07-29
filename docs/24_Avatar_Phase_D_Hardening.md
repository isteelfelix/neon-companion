# 24_Avatar_Phase_D_Hardening.md

## Scope and result

Phase D hardens the approved Companion/avatar Phase A–C surface. It does not add
formats, Live2D, or provider/TTS/STT routes.

The audit found and fixed four runtime gaps:

- import now rechecks every validated source file's size and last-write time
  immediately before copy, rejects duplicate destinations, and leaves no partial
  custom directory on failure;
- GLB/glTF/VRM catalog node, renderer-instance, triangle, and animation estimates
  are rejected before the expensive Unity importer runs; decoded runtime facts
  remain the final authority;
- generic 3D keeps at most one inactive cached template instead of retaining every
  model selected during the process lifetime;
- stop/cancel/interrupt/barge-in completes the active TTS wait locally. A voice
  backend that never emits `OnPlaybackComplete` can no longer stall later speech
  for the 30-minute safety timeout.

## Supported input boundary

The supported formats remain intentionally narrow:

| Backend | Accepted input | Limits and exclusions |
|---|---|---|
| static 2D | `.png`, `.jpg`, `.jpeg` | 20 MB; `8192 x 8192`; full decode must succeed; no GIF/WebP |
| sprite sheet | `motion_pack.json` v1 plus local PNG sheets | 24 clips; 100 MB bundle; 64 MP decoded total; each sheet `8192 x 8192`; exact grid division; no remote/absolute/symlink sidecars |
| generic 3D | glTF 2.0 `.glb` or `.gltf` | 100 MB local bundle; 512 nodes; 128 renderer instances; 500,000 triangles; 128 animations; local `buffers[].uri`/`images[].uri` or data URI only; no ZIP/remote URI |
| VRM | `.vrm` containing VRM 0.x or VRM 1.0 metadata | 100 MB; 512 nodes; 128 renderer instances; 500,000 triangles; 128 embedded animations; only detected humanoid/expression features are enabled |

VRMA remains a packaged state-animation resource, not a new user import format.
Generic GLB/glTF does not gain VRM expressions or lipsync by filename/content
guessing. A `.glb` containing VRM metadata is rejected and must be imported
explicitly as `.vrm`.

## Executable regression coverage

Linux/static:

```bash
python3 Tools/verify_avatar_phase_a.py
python3 Tools/verify_avatar_phase_b.py
python3 Tools/verify_avatar_phase_c.py
python3 Tools/verify_avatar_phase_d.py
```

Unity EditMode tests in `Assets/Tests/EditMode/AvatarPhaseDTests.cs` cover:

- legacy static and sprite profile normalization;
- generic 3D state mapping and future-contract rejection;
- source mutation between inspection and copy, plus size rejection before decode;
- catalog-limit and malformed-container rejection before runtime instantiation;
- actual glTFast loading of generated triangle `.gltf` and `.glb` mappings;
- immediate TTS stop followed by replay when the fake backend omits completion;
- display snapshot exclusion of persona/transport state.

The explicit `VrmZeroAndOneFixturesLoadThroughUniVrm` test loads the committed
VRM 1.0 fixture plus a licensed external VRM 0.x fixture. Before running it, set
`NEON_PHASE_D_VRM0_FIXTURE` to the local VRM 0.x path. The external asset is not
committed because its license belongs to the fixture owner.

## Compatibility evidence and honest blockers

The committed `Neon.vrm.bytes` parses as VRM 1.0 and all six packaged
`.vrma.bytes` files parse as `VRMC_vrm_animation`. Unity EditMode verifies that
both the model and animation are runtime-imported with live control rigs.
Unity PlayMode runs the idle VRMA for 30 frames with no unexpected log messages.
A second licensed VRM 0.x model remains an explicit external-fixture test.

Generic compatibility has two generated mappings (`.gltf` with embedded data URI
and binary `.glb`) in the Unity EditMode test. Their glTFast execution is likewise
Unity-gated; the runner only verifies that the tests and valid containers exist.

## Performance and memory observations

One Linux run on 2026-07-29 reported:

- `Neon.vrm.bytes`: 16,587,980 bytes, 122,324-byte JSON catalog, 149 nodes,
  3 renderer instances, estimated 48,557 triangles; Python catalog parse about
  17–33 ms across two runs with about 0.66 MiB traced peak. This is parser
  evidence, not Unity memory.
- six VRMA files: 896,784 bytes total; slowest Python catalog parse under 10 ms
  in those runs.
- six motion PNGs: Git LFS pointers on this runner, 73,086,066 logical compressed
  bytes total. Dimensions, decoded RGBA cost, GPU upload, and Unity sprite cache
  could not be measured without the LFS objects and Unity.

The one-template generic cache provides a deterministic memory ceiling for cached
models; the active model and importer allocations still depend on asset content
within the documented limits.

Felix records the remaining observations with the Windows Development Player and
Unity Profiler:

1. capture main-process Memory and CPU after 60 seconds on legacy static and
   sprite profiles;
2. select generated GLB/glTF and both VRM fixtures five times each; record peak
   and post-unload reserved memory and confirm it does not grow per unique generic
   model beyond one cached template;
3. enable Companion and record both process working sets/GPU memory for idle,
   speaking, and immediate stop/barge-in;
4. attach profiler screenshots/logs and note fixture hashes in the acceptance
   evidence directory.

## Windows acceptance ledger

`Tools/companion_windows_acceptance.json` is the machine-checked ten-item ledger.
Every item records its executable coverage, current runner blocker, and required
Felix evidence. `Tools/Test-CompanionWindowsAcceptance.ps1` automates process
count, IPC, child isolation, child-crash survival, preference restart, protected
JSON hashes, and parent/child cleanup without modifying settings.
