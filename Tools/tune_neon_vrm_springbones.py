#!/usr/bin/env python3
"""Apply and verify the built-in Neon VRM secondary-motion profile."""

import argparse
import json
import pathlib
import struct
import tempfile


ROOT = pathlib.Path(__file__).resolve().parents[1]
VRM_PATH = ROOT / "Assets/Resources/Avatars/neon/Neon.vrm.bytes"

COAT_STIFFNESS = 0.28
COAT_DRAG = 0.22
COAT_GRAVITY = 0.08
EXPECTED_COAT_SPRINGS = 18
EXPECTED_COAT_JOINTS = 42


def read_glb(path):
    raw = path.read_bytes()
    if len(raw) < 20:
        raise ValueError("incomplete GLB header")
    magic, version, declared_length = struct.unpack_from("<III", raw, 0)
    if magic != 0x46546C67 or version != 2 or declared_length != len(raw):
        raise ValueError("invalid GLB header")

    json_length, json_type = struct.unpack_from("<II", raw, 12)
    if json_type != 0x4E4F534A:
        raise ValueError("first GLB chunk is not JSON")
    json_end = 20 + json_length
    document = json.loads(raw[20:json_end].decode("utf-8").rstrip(" \0\t\r\n"))
    return document, raw[json_end:]


def coat_springs(document):
    nodes = document["nodes"]
    spring_bone = document["extensions"]["VRMC_springBone"]
    matches = []
    for spring in spring_bone["springs"]:
        joints = spring.get("joints", [])
        if joints and "CoatSkirt" in nodes[joints[0]["node"]].get("name", ""):
            matches.append(spring)
    return matches


def apply_profile(document):
    nodes = document["nodes"]
    springs = coat_springs(document)
    tuned_joints = 0
    for spring in springs:
        spring["name"] = "CoatSkirt"
        for joint in spring["joints"]:
            node_name = nodes[joint["node"]].get("name", "")
            if node_name.endswith("_end"):
                continue
            joint["stiffness"] = COAT_STIFFNESS
            joint["dragForce"] = COAT_DRAG
            joint["gravityPower"] = COAT_GRAVITY
            joint["gravityDir"] = [0.0, -1.0, 0.0]
            tuned_joints += 1
    return len(springs), tuned_joints


def verify_profile(document):
    nodes = document["nodes"]
    springs = coat_springs(document)
    assert len(springs) == EXPECTED_COAT_SPRINGS, len(springs)
    tuned_joints = 0
    for spring in springs:
        assert spring.get("name") == "CoatSkirt"
        for joint in spring["joints"]:
            node_name = nodes[joint["node"]].get("name", "")
            if node_name.endswith("_end"):
                continue
            assert joint.get("stiffness") == COAT_STIFFNESS, node_name
            assert joint.get("dragForce") == COAT_DRAG, node_name
            assert joint.get("gravityPower") == COAT_GRAVITY, node_name
            assert joint.get("gravityDir") == [0.0, -1.0, 0.0], node_name
            tuned_joints += 1
    assert tuned_joints == EXPECTED_COAT_JOINTS, tuned_joints
    return len(springs), tuned_joints


def write_glb(path, document, remaining_chunks):
    json_bytes = json.dumps(
        document,
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")
    json_bytes += b" " * ((-len(json_bytes)) % 4)
    total_length = 20 + len(json_bytes) + len(remaining_chunks)
    header = struct.pack(
        "<IIIII",
        0x46546C67,
        2,
        total_length,
        len(json_bytes),
        0x4E4F534A,
    )

    with tempfile.NamedTemporaryFile(
        dir=path.parent,
        prefix=path.name + ".",
        suffix=".tmp",
        delete=False,
    ) as stream:
        temporary_path = pathlib.Path(stream.name)
        stream.write(header)
        stream.write(json_bytes)
        stream.write(remaining_chunks)
    temporary_path.replace(path)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--check",
        action="store_true",
        help="verify the profile without changing the VRM",
    )
    args = parser.parse_args()

    document, remaining_chunks = read_glb(VRM_PATH)
    if not args.check:
        spring_count, joint_count = apply_profile(document)
        if spring_count != EXPECTED_COAT_SPRINGS or joint_count != EXPECTED_COAT_JOINTS:
            raise AssertionError(
                f"unexpected coat inventory: {spring_count} springs, {joint_count} joints"
            )
        write_glb(VRM_PATH, document, remaining_chunks)
        document, _ = read_glb(VRM_PATH)

    spring_count, joint_count = verify_profile(document)
    print(
        f"Neon coat physics OK: {spring_count} springs, {joint_count} tuned joints"
    )


if __name__ == "__main__":
    main()
