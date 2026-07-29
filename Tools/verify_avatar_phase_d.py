#!/usr/bin/env python3
"""Executable Phase D fixture, regression, and acceptance-evidence checks."""

import hashlib
import json
import pathlib
import re
import struct
import time
import tracemalloc


ROOT = pathlib.Path(__file__).resolve().parents[1]
MAX_MODEL_BYTES = 100 * 1024 * 1024
MAX_NODES = 512
MAX_RENDERERS = 128
MAX_TRIANGLES = 500_000
MAX_ANIMATIONS = 128


def read(path):
    return (ROOT / path).read_text(encoding="utf-8")


def parse_glb(path):
    started = time.perf_counter()
    tracemalloc.start()
    with path.open("rb") as stream:
        header = stream.read(20)
        assert len(header) == 20, f"incomplete GLB header: {path}"
        magic, version, declared_length, json_length, json_type = struct.unpack(
            "<IIIII", header
        )
        assert magic == 0x46546C67, f"wrong GLB magic: {path}"
        assert version == 2, f"wrong GLB version: {path}"
        assert declared_length == path.stat().st_size, f"wrong GLB length: {path}"
        assert json_type == 0x4E4F534A, f"missing GLB JSON chunk: {path}"
        assert 0 < json_length <= declared_length - 20
        document = json.loads(
            stream.read(json_length).decode("utf-8").rstrip("\0 \t\r\n")
        )
    _, peak = tracemalloc.get_traced_memory()
    tracemalloc.stop()
    return document, {
        "fileBytes": path.stat().st_size,
        "jsonBytes": json_length,
        "parseMilliseconds": round((time.perf_counter() - started) * 1000, 2),
        "pythonPeakBytes": peak,
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
    }


def accessor_count(document, token):
    accessors = document.get("accessors", [])
    if not isinstance(token, int) or not 0 <= token < len(accessors):
        return 0
    value = accessors[token].get("count", 0)
    return value if isinstance(value, int) and value > 0 else 0


def primitive_triangles(document, primitive):
    mode = primitive.get("mode", 4)
    if mode not in (4, 5, 6):
        return 0
    token = primitive.get("indices")
    if token is None:
        token = primitive.get("attributes", {}).get("POSITION")
    count = accessor_count(document, token)
    return count // 3 if mode == 4 else max(0, count - 2)


def catalog_facts(document):
    meshes = document.get("meshes", [])
    uses = [0] * len(meshes)
    for node in document.get("nodes", []):
        mesh = node.get("mesh") if isinstance(node, dict) else None
        if isinstance(mesh, int) and 0 <= mesh < len(uses):
            uses[mesh] += 1
    renderer_estimate = 0
    triangle_estimate = 0
    for index, mesh in enumerate(meshes):
        instances = max(1, uses[index])
        renderer_estimate += instances
        triangle_estimate += instances * sum(
            primitive_triangles(document, primitive)
            for primitive in mesh.get("primitives", [])
        )
    return {
        "nodes": len(document.get("nodes", [])),
        "rendererInstances": renderer_estimate,
        "triangles": triangle_estimate,
        "animations": len(document.get("animations", [])),
    }


def within_catalog_limits(facts):
    return (
        facts["nodes"] <= MAX_NODES
        and facts["rendererInstances"] <= MAX_RENDERERS
        and facts["triangles"] <= MAX_TRIANGLES
        and facts["animations"] <= MAX_ANIMATIONS
    )


def png_size(path):
    with path.open("rb") as stream:
        header = stream.read(24)
    assert header[:8] == b"\x89PNG\r\n\x1a\n", f"invalid PNG: {path}"
    assert header[12:16] == b"IHDR", f"missing PNG IHDR: {path}"
    return struct.unpack(">II", header[16:24])


def lfs_pointer(path):
    raw = path.read_bytes()
    prefix = b"version https://git-lfs.github.com/spec/v1\n"
    if not raw.startswith(prefix):
        return None
    text = raw.decode("utf-8")
    match = re.search(r"^oid sha256:([0-9a-f]{64})\nsize ([0-9]+)$", text, re.MULTILINE)
    assert match, f"invalid Git LFS pointer: {path}"
    return {"sha256": match.group(1), "logicalBytes": int(match.group(2))}


def verify_acceptance_matrix():
    matrix = json.loads(read("Tools/companion_windows_acceptance.json"))
    assert matrix["schemaVersion"] == 1
    assert matrix["runner"]["status"] == "environment_blocked"
    criteria = matrix["criteria"]
    assert len(criteria) == 10, "Windows acceptance matrix must contain exactly ten criteria"
    assert [item["id"] for item in criteria] == [
        f"WIN-{index:02d}" for index in range(1, 11)
    ]
    for item in criteria:
        assert item["runnerStatus"].startswith("blocked_"), item["id"]
        assert item["executableCoverage"], item["id"]
        assert item["felixEvidence"], item["id"]
        for reference in item["executableCoverage"]:
            path_text, separator, symbol = reference.partition("::")
            path = ROOT / path_text
            assert path.is_file(), f"{item['id']} missing executable coverage: {path_text}"
            if separator:
                assert symbol in path.read_text(encoding="utf-8"), (
                    f"{item['id']} missing test symbol: {symbol}"
                )


def verify_assets_and_runtime():
    vrm_path = ROOT / "Assets/Resources/Avatars/neon/Neon.vrm"
    vrm, observation = parse_glb(vrm_path)
    assert vrm_path.stat().st_size <= MAX_MODEL_BYTES
    assert "VRMC_vrm" in vrm.get("extensions", {})
    assert vrm["extensions"]["VRMC_vrm"].get("specVersion", "").startswith("1.")
    facts = catalog_facts(vrm)
    assert within_catalog_limits(facts), facts
    observation.update(facts)
    observation["fixture"] = str(vrm_path.relative_to(ROOT))

    states = ("idle", "thinking", "talking", "listening", "smile", "confused")
    vrma_observations = []
    for state in states:
        path = ROOT / f"Assets/Resources/Avatars/neon/Neon_{state}.vrma"
        document, item = parse_glb(path)
        assert "VRMC_vrm_animation" in document.get("extensions", {})
        assert len(document.get("animations", [])) == 1
        item["state"] = state
        vrma_observations.append(item)

    sheet_paths = sorted(
        (ROOT / "Assets/Resources/Avatars/neon").glob("*_sheet.png")
    )
    assert len(sheet_paths) >= 6
    decoded_pixels = 0
    compressed_bytes = 0
    lfs_pointers = []
    for path in sheet_paths:
        pointer = lfs_pointer(path)
        if pointer is not None:
            pointer["file"] = path.name
            lfs_pointers.append(pointer)
            compressed_bytes += pointer["logicalBytes"]
            continue
        width, height = png_size(path)
        assert 0 < width <= 8192 and 0 < height <= 8192
        decoded_pixels += width * height
        compressed_bytes += path.stat().st_size
    if not lfs_pointers:
        assert decoded_pixels <= 64_000_000

    too_many_nodes = {"nodes": [{} for _ in range(MAX_NODES + 1)]}
    assert not within_catalog_limits(catalog_facts(too_many_nodes))
    too_many_triangles = {
        "nodes": [{"mesh": 0}],
        "meshes": [{"primitives": [{"indices": 0}]}],
        "accessors": [{"count": (MAX_TRIANGLES + 1) * 3}],
    }
    assert not within_catalog_limits(catalog_facts(too_many_triangles))
    instanced_over_limit = {
        "nodes": [{"mesh": 0} for _ in range(MAX_RENDERERS + 1)],
        "meshes": [{"primitives": []}],
    }
    assert not within_catalog_limits(catalog_facts(instanced_over_limit))

    return {
        "vrm": observation,
        "vrma": {
            "count": len(vrma_observations),
            "totalBytes": sum(item["fileBytes"] for item in vrma_observations),
            "slowestParseMilliseconds": max(
                item["parseMilliseconds"] for item in vrma_observations
            ),
            "maxPythonPeakBytes": max(
                item["pythonPeakBytes"] for item in vrma_observations
            ),
        },
        "spriteSheets": {
            "count": len(sheet_paths),
            "compressedBytes": compressed_bytes,
            "decodedPixelsAvailableOnRunner": decoded_pixels,
            "rgbaObservedBytesAvailableOnRunner": decoded_pixels * 4,
            "gitLfsPointers": lfs_pointers,
        },
    }


def verify_implementation_contracts():
    importer = read("Assets/Scripts/Runtime/Avatar/AvatarAssetImporter.cs")
    loader = read("Assets/Scripts/Runtime/Avatar3D/Avatar3DLoader.cs")
    voice = read("Assets/Scripts/Runtime/Voice/VoiceOutputManager.cs")
    tests = read("Assets/Tests/EditMode/AvatarPhaseDTests.cs")
    powershell = read("Tools/Test-CompanionWindowsAcceptance.ps1")
    parent_runtime = read(
        "Assets/Scripts/Runtime/Platform/WindowsCompanionWindowService.cs"
    )
    child_runtime = read(
        "Assets/Scripts/Runtime/Platform/CompanionPlayerRuntime.cs"
    )
    avatar_ui = read("Assets/UI/Avatars/AvatarsView.uxml")

    for marker in (
        "ValidateImportFiles(",
        "validatedLength",
        "validatedLastWriteUtcTicks",
        '"source_changed"',
        "ValidateGltfCatalog(",
        "EstimatePrimitiveTriangles(",
    ):
        assert marker in importer, f"missing import hardening: {marker}"
    assert importer.index("ValidateGltfCatalog(result, document)") < importer.index(
        "Avatar3DLoader.LoadAsync(result.sourcePath)"
    )
    assert "_cachedModel" in loader and "_cachedPath" in loader
    assert "Dictionary<string, CachedModel> Cache" not in loader
    assert "_activePlaybackCompletion.TrySetResult(true)" in voice
    assert "ReferenceEquals(_activePlaybackCompletion, tcs)" in voice

    required_tests = (
        "LegacyStaticAndSpriteProfilesRemainReadable",
        "GenericMappingAndFutureContractFallbackStayDeterministic",
        "CompanionDockStateMachineCoversDetachHideRecoveryAndReturn",
        "DockDetachDoesNotMutateProfileSessionOrVoiceRoute",
        "CompanionPetPreferencesPreserveVisiblePinAndScale",
        "ChangedSourceIsRejectedBeforeCopy",
        "TemporaryPreviewObjectsUseEditModeSafeCleanup",
        "OversizedImageIsRejectedBeforeDecode",
        "CatalogLimitsRejectWorkBeforeRuntimeInstantiation",
        "GenericGltfAndGlbMappingsLoadThroughRuntime",
        "StopCancelsBackendWaitAndAllowsImmediateReplay",
        "VrmZeroAndOneFixturesLoadThroughUniVrm",
    )
    for name in required_tests:
        assert name in tests, f"missing Unity regression: {name}"
    assert "NEON_PHASE_D_VRM0_FIXTURE" in tests
    assert "[Explicit(" in tests
    assert "Get-CimInstance Win32_Process" in powershell
    assert "CloseMainWindow()" in powershell
    assert "protectedDataHashes" in powershell
    for reason in (
        "PROCESS_SPAWN_TIMEOUT",
        "PIPE_CONNECTION",
        "RUNTIME_READY",
        "WINDOW_RESPONSIVENESS_TIMEOUT",
    ):
        assert reason in powershell, f"missing Windows timeout diagnostic: {reason}"
    assert "WaitForInputIdle(1000)" in powershell
    assert "SendMessageTimeout" in powershell
    assert "timeout-window-responsiveness" in powershell
    assert '"runtime_ready"' in parent_runtime and '"heartbeat"' in parent_runtime
    assert "WaitForConnectionAsync" in parent_runtime
    assert "ReadClientAsync(reader, pipe, token)" in parent_runtime
    accept_client = parent_runtime[parent_runtime.index("private async Task AcceptClientAsync"):]
    assert accept_client.index("ReadClientAsync(reader, pipe, token)") < accept_client.index(
        "SendProfileAndPreferences();"
    )
    read_completed = "if (completed == readTask && !_runtimeReady)"
    assert read_completed in accept_client
    assert "IPC disconnected before runtime-ready handshake." in accept_client
    assert accept_client.index(read_completed) < accept_client.index("await readTask;")
    assert '"runtime_ready"' in child_runtime and '"heartbeat"' in child_runtime
    assert "WriteLoopAsync" in child_runtime
    assert "Generic3DEnabled = false" in loader
    assert "avatar-import-3d-btn" not in avatar_ui
    assert "DestroyTemporaryObject" in importer

    asmdef = json.loads(
        read("Assets/Tests/EditMode/NeonCompanion.EditModeTests.asmdef")
    )
    assert "TestAssemblies" in asmdef["optionalUnityReferences"]
    assert "GUID:30d9501bacecd4e4485f7b01a8ff7b4b" in asmdef["references"]
    for path in (
        "Assets/Tests.meta",
        "Assets/Tests/EditMode.meta",
        "Assets/Tests/EditMode/NeonCompanion.EditModeTests.asmdef.meta",
        "Assets/Tests/EditMode/AvatarPhaseDTests.cs.meta",
    ):
        assert (ROOT / path).is_file(), f"missing Unity meta: {path}"

    english = json.loads(read("Assets/Resources/Localization/en.json"))
    russian = json.loads(read("Assets/Resources/Localization/ru.json"))
    for code in ("inspection_required", "source_changed", "duplicate_destination"):
        key = "avatar.import.error." + code
        assert key in english and key in russian

    new_csharp = "\n".join((importer, loader, voice, tests))
    code_only = re.sub(
        r'//[^\n]*|"(?:\\.|[^"\\])*"|/\*.*?\*/',
        "",
        new_csharp,
        flags=re.DOTALL,
    )
    for pattern, name in (
        (r"\bis\s+not\s+", "is not"),
        (r"\bis\s+null\b", "is null"),
        (r"=\s*new\s*\(", "target-typed new"),
        (r"\bswitch\s*\{", "switch expression"),
    ):
        assert not re.search(pattern, code_only), f"unsupported C# syntax: {name}"


verify_acceptance_matrix()
observations = verify_assets_and_runtime()
verify_implementation_contracts()
print("avatar Phase D hardening: ok")
print(json.dumps(observations, indent=2, sort_keys=True))
