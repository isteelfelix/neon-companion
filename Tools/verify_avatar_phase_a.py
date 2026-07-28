#!/usr/bin/env python3
"""Focused static checks for the Phase A avatar persistence/UI contract."""

import json
import pathlib
import re
import xml.etree.ElementTree as ET


ROOT = pathlib.Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8")


profile = read("Assets/Scripts/Runtime/Data/Models/AvatarProfile.cs")
repository = read("Assets/Scripts/Runtime/Data/Repositories/AvatarRepository.cs")
importer = read("Assets/Scripts/Runtime/Avatar/AvatarAssetImporter.cs")
loader = read("Assets/Scripts/Runtime/Avatar3D/Avatar3DLoader.cs")
controller = read("Assets/Scripts/Runtime/UI/UITK/AvatarGalleryController.cs")
uss = read("Assets/UI/Avatars/AvatarsView.uss")

for field in (
    "contractVersion",
    "avatarType",
    "source",
    "capabilities",
    "stateClipMapping",
    "diagnostic",
    "isVerified",
):
    assert "public " in profile and field in profile, "missing profile field: " + field

assert repository.count("NormalizeContract()") >= 2
assert 'public const string Vrm = "vrm"' in profile
assert "vrm_restricted_features" in importer
assert "DeleteImportDirectory" in importer
assert "TryBuildClipMapping" in controller

manifest = json.loads(read("Packages/manifest.json"))
lock = json.loads(read("Packages/packages-lock.json"))
assert manifest["dependencies"]["com.unity.cloud.gltfast"] == "6.14.1"
assert lock["dependencies"]["com.unity.cloud.gltfast"]["depth"] == 0
linker = ET.parse(ROOT / "Assets/Scripts/Runtime/Avatar3D/link.xml").getroot()
assert any(node.attrib.get("fullname") == "glTFast" for node in linker.findall("assembly"))

english = json.loads(read("Assets/Resources/Localization/en.json"))
russian = json.loads(read("Assets/Resources/Localization/ru.json"))
phase_keys = {key for key in english if key.startswith(("avatar.import.", "avatar.capability.", "avatar.type.", "avatar.vrm."))}
assert phase_keys
assert phase_keys <= russian.keys()
assert {key for key in russian if key.startswith(("avatar.import.", "avatar.capability.", "avatar.type.", "avatar.vrm."))} <= english.keys()
for code in re.findall(r'Fail\(result,\s*"([^"]+)"', importer):
    assert "avatar.import.error." + code in english, "missing import error localization: " + code
for code in re.findall(r'ErrorCode\s*=\s*"([^"]+)"', loader):
    assert "avatar.import.error." + code in english, "missing 3D error localization: " + code
for evidence in re.findall(r'evidence\.Add\("([^"]+)"\)', importer):
    key = "avatar.capability.evidence." + evidence
    assert key in english, "missing capability evidence localization: " + evidence

xml_root = ET.parse(ROOT / "Assets/UI/Avatars/AvatarsView.uxml").getroot()
names = {element.attrib.get("name") for element in xml_root.iter()}
for name in (
    "avatar-import-overlay",
    "avatar-import-static-btn",
    "avatar-import-sprite-btn",
    "avatar-import-3d-btn",
    "avatar-import-vrm-btn",
    "avatar-import-preview-image",
    "avatar-import-save-btn",
    "avatar-capabilities-foldout",
):
    assert name in names, "missing UI element: " + name

for unsupported in (
    "z-index:",
    "gap:",
    "line-height:",
    "pointer-events:",
    "box-shadow:",
    "!important",
    "calc(",
    "@media",
    "@keyframes",
):
    assert unsupported not in uss, "unsupported USS: " + unsupported

print("avatar Phase A static contract: ok")
