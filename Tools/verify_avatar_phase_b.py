#!/usr/bin/env python3
"""Focused static checks for the VRM Phase B runtime contract."""

import json
import pathlib
import re
import struct
import xml.etree.ElementTree as ET


ROOT = pathlib.Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8")


def parse_glb_json(path):
    with path.open("rb") as stream:
        header = stream.read(20)
        assert len(header) == 20
        magic, version, declared_length, json_length, json_type = struct.unpack(
            "<IIIII", header
        )
        assert magic == 0x46546C67 and version == 2
        assert declared_length == path.stat().st_size
        assert json_type == 0x4E4F534A
        return json.loads(
            stream.read(json_length).decode("utf-8").rstrip("\0 \t\r\n")
        )


manifest = json.loads(read("Packages/manifest.json"))
lock = json.loads(read("Packages/packages-lock.json"))
for package in ("com.vrmc.gltf", "com.vrmc.vrm"):
    expected = "file:" + package
    assert manifest["dependencies"][package] == expected
    assert lock["dependencies"][package]["version"] == expected
    assert lock["dependencies"][package]["source"] == "embedded"

vrm_package = json.loads(read("Packages/com.vrmc.vrm/package.json"))
assert vrm_package["version"] == "0.131.2"
asmdef = json.loads(read("Assets/Scripts/Runtime/NeonCompanion.Runtime.asmdef"))
assert "GUID:e47c917724578cc43b5506c17a27e9a0" in asmdef["references"]
linker = ET.parse(ROOT / "Assets/Scripts/Runtime/Avatar3D/link.xml").getroot()
assert any(node.attrib.get("fullname") == "VRM10" for node in linker.findall("assembly"))
graphics_settings = read("ProjectSettings/GraphicsSettings.asset")
assert (
    "{fileID: 46, guid: 0000000000000000f000000000000000, type: 0}"
    in graphics_settings
), "UniVRM animation debug material requires the built-in Standard shader"
assert (
    "933532a4fcc9baf4fa0491de14d08ed7" in graphics_settings
), "UniVRM animation debug material requires the URP Lit shader"
for shader_guid in (
    "8c17b56f4bf084c47872edcb95237e4a",
):
    assert shader_guid in graphics_settings, "runtime avatar shader can be stripped"

loader = read("Assets/Scripts/Runtime/Avatar3D/Avatar3DLoader.cs")
service = read("Assets/Scripts/Runtime/Avatar3D/Avatar3DService.cs")
importer = read("Assets/Scripts/Runtime/Avatar/AvatarAssetImporter.cs")
gallery = read("Assets/Scripts/Runtime/UI/UITK/AvatarGalleryController.cs")
runtime_ui_installer = read(
    "Assets/Scripts/Runtime/UI/UITK/RuntimeUiInstaller.cs"
)
boot_scene_loader = read("Assets/Scripts/Runtime/Core/BootSceneLoader.cs")
lipsync = read("Assets/Scripts/Runtime/Voice/LipsyncController.cs")
voice_output = read("Assets/Scripts/Runtime/Voice/VoiceOutputManager.cs")
chat = read("Assets/Scripts/Runtime/UI/UITK/ChatController.cs")

assert 'if (ext == ".vrm")' in loader
assert "CompanionProcessMode.IsPlayerProcess" in runtime_ui_installer
assert "CompanionProcessMode.IsPlayerProcess" in boot_scene_loader
assert "Vrm10.LoadPathAsync(" in loader
assert "Vrm10.LoadBytesAsync" in loader
assert (
    loader.count("GetValidGltfMaterialDescriptorGenerator") >= 2
), "VRM runtime imports must use the active pipeline's visible glTF materials"
assert "LoadBuiltInVrmAnimationAsync" in loader
assert "UniVRM is deliberately called only for the .vrm extension" in loader
for capability in (
    "hasHumanoid",
    "hasBlink",
    "hasGaze",
    "hasExpressions",
    "hasLipsync",
    "isRestricted",
):
    assert capability in loader, "missing extracted capability: " + capability
assert "univrm_0_131_2_runtime" in loader
assert "Vrm10AnimationInstance" in service
assert "SetMouthShape" in service and "ClearMouth" in service
assert "InspectVrmAsync" in importer and "Avatar3DLoader.LoadAsync" in importer
assert "SelectVrmAvatarAsync" in gallery
assert "The current avatar was preserved" in gallery
assert "getAvatar3DService" in lipsync
viseme_pairs = dict(re.findall(r"\['(.)'\]\s*=\s*Viseme\.([AEIOU])", lipsync))
for character, viseme in {
    "a": "A", "e": "E", "i": "I", "o": "O", "u": "U",
    "а": "A", "я": "A", "е": "E", "э": "E", "и": "I", "ы": "I",
    "о": "O", "ё": "O", "у": "U", "ю": "U",
}.items():
    assert viseme_pairs.get(character) == viseme, "missing viseme mapping: " + character
assert "_playbackGeneration++" in voice_output
assert "_d.StopVoiceOutput?.Invoke();" in chat

fixture = ROOT / "Assets/Resources/Avatars/neon/Neon.vrm.bytes"
fixture_meta = ROOT / "Assets/Resources/Avatars/neon/Neon.vrm.bytes.meta"
assert fixture.is_file() and fixture.stat().st_size > 0
fixture_contract = parse_glb_json(fixture)
fixture_meta_contract = fixture_contract["extensions"]["VRMC_vrm"]["meta"]
assert "Felix" in fixture_meta_contract["authors"]
assert fixture_meta_contract["allowRedistribution"] is True
assert fixture_meta.is_file()
assert "TextScriptImporter:" in fixture_meta.read_text(encoding="utf-8")
for state in ("idle", "thinking", "talking", "listening", "smile", "confused"):
    asset = ROOT / (
        "Assets/Resources/Avatars/neon/Neon_" + state + ".vrma.bytes"
    )
    assert asset.is_file() and asset.stat().st_size > 0, "missing VRMA: " + state
    assert pathlib.Path(str(asset) + ".meta").is_file(), "missing VRMA meta: " + state

english = json.loads(read("Assets/Resources/Localization/en.json"))
russian = json.loads(read("Assets/Resources/Localization/ru.json"))
for key in (
    "avatar.capability.humanoid",
    "avatar.capability.blink",
    "avatar.capability.gaze",
    "avatar.capability.expressions",
    "avatar.capability.evidence.univrm_0_131_2_runtime",
    "avatar.vrm.restricted",
    "avatar.vrm.invalid.preserved",
):
    assert key in english and key in russian, "missing localization: " + key

print("avatar Phase B static contract: ok")
