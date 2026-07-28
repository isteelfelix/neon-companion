#!/usr/bin/env python3
"""Focused static checks for the VRM Phase B runtime contract."""

import json
import pathlib
import re
import xml.etree.ElementTree as ET


ROOT = pathlib.Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8")


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

loader = read("Assets/Scripts/Runtime/Avatar3D/Avatar3DLoader.cs")
service = read("Assets/Scripts/Runtime/Avatar3D/Avatar3DService.cs")
importer = read("Assets/Scripts/Runtime/Avatar/AvatarAssetImporter.cs")
gallery = read("Assets/Scripts/Runtime/UI/UITK/AvatarGalleryController.cs")
lipsync = read("Assets/Scripts/Runtime/Voice/LipsyncController.cs")
voice_output = read("Assets/Scripts/Runtime/Voice/VoiceOutputManager.cs")
chat = read("Assets/Scripts/Runtime/UI/UITK/ChatController.cs")

assert 'if (ext == ".vrm")' in loader
assert "Vrm10.LoadPathAsync(fullPath, true)" in loader
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

fixture = ROOT / "Assets/Resources/Avatars/neon/Neon.vrm"
fixture_meta = ROOT / "Assets/Resources/Avatars/neon/Neon.vrm.meta"
assert fixture.is_file() and fixture.stat().st_size > 0
fixture_contract = read("Assets/Resources/Avatars/neon/Neon.vrm1.Assets/_vrm1_.asset")
assert "Authors:\n    - Felix" in fixture_contract
assert "Redistribution: 1" in fixture_contract
assert fixture_meta.is_file()
for state in ("idle", "thinking", "talking", "listening", "smile", "confused"):
    asset = ROOT / ("Assets/Resources/Avatars/neon/Neon_" + state + ".vrma")
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
