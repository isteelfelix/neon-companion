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
BUST_STIFFNESS = 0.32
BUST_DRAG = 0.22
BUST_GRAVITY = 0.06
EXPECTED_BUST_SPRINGS = 2
EXPECTED_BUST_JOINTS = 4
BODY_MATERIAL = "N00_000_00_Body_00_SKIN (Instance)"
EXPECTED_COVERED_BODY_TRIANGLES = 800

# Body triangles that are fully behind opaque pixels of the outer grey top in
# the authored rest pose. The body texture contains the black bra, so leaving
# these triangles renderable lets the body pierce the top when the Bust chains
# move. Degenerating only the covered triangles preserves the exposed neckline
# and the original skin weights of every garment layer.
COVERED_BODY_TRIANGLE_RANGES = (
    (744, 744), (813, 814), (817, 818), (831, 831), (840, 841),
    (844, 844), (846, 847), (850, 851), (854, 854), (856, 856),
    (859, 866), (871, 872), (876, 876), (879, 886), (919, 922),
    (925, 925), (927, 929), (931, 932), (934, 941), (944, 946),
    (948, 948), (952, 953), (956, 958), (960, 960), (1042, 1042),
    (1055, 1056), (1237, 1237), (1247, 1248), (1250, 1251),
    (1266, 1268), (1275, 1302), (1305, 1315), (1318, 1318),
    (1387, 1387), (1401, 1402), (1404, 1404), (1409, 1411),
    (1413, 1415), (1420, 1421), (1424, 1463), (1465, 1466),
    (1468, 1469), (1472, 1474), (1476, 1477), (1481, 1481),
    (1484, 1484), (1581, 1581), (1586, 1591), (1593, 1596),
    (1599, 1599), (1604, 1612), (1617, 1622), (1625, 1629),
    (1633, 1636), (1641, 1641), (1665, 1665), (1679, 1679),
    (4476, 4852), (4855, 4856), (4858, 4858), (4860, 5064),
)


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


def bust_springs(document):
    nodes = document["nodes"]
    spring_bone = document["extensions"]["VRMC_springBone"]
    matches = []
    for spring in spring_bone["springs"]:
        joints = spring.get("joints", [])
        if joints and "Bust" in nodes[joints[0]["node"]].get("name", ""):
            matches.append(spring)
    return matches


def covered_body_triangles():
    triangles = []
    for first, last in COVERED_BODY_TRIANGLE_RANGES:
        triangles.extend(range(first, last + 1))
    assert len(triangles) == EXPECTED_COVERED_BODY_TRIANGLES
    assert len(set(triangles)) == EXPECTED_COVERED_BODY_TRIANGLES
    return triangles


def body_index_accessor(document):
    for mesh in document["meshes"]:
        for primitive in mesh["primitives"]:
            material = document["materials"][primitive["material"]]
            if material.get("name") == BODY_MATERIAL:
                accessor = document["accessors"][primitive["indices"]]
                if accessor["componentType"] != 5123:
                    raise AssertionError("Neon body indices are no longer unsigned shorts")
                if accessor["type"] != "SCALAR" or accessor["count"] != 27351:
                    raise AssertionError("unexpected Neon body index inventory")
                return accessor
    raise AssertionError("Neon body primitive not found")


def body_index_offset(document, accessor):
    view = document["bufferViews"][accessor["bufferView"]]
    if view.get("byteStride", 2) != 2:
        raise AssertionError("unexpected Neon body index stride")
    return view.get("byteOffset", 0) + accessor.get("byteOffset", 0)


def mask_covered_body(document, remaining_chunks):
    accessor = body_index_accessor(document)
    offset = body_index_offset(document, accessor)
    chunks = bytearray(remaining_chunks)
    # remaining_chunks starts with the eight-byte BIN chunk header.
    for triangle in covered_body_triangles():
        first_index_offset = 8 + offset + triangle * 6
        first_index = struct.unpack_from("<H", chunks, first_index_offset)[0]
        struct.pack_into("<HHH", chunks, first_index_offset, first_index, first_index, first_index)
    return bytes(chunks), EXPECTED_COVERED_BODY_TRIANGLES


def verify_body_mask(document, remaining_chunks):
    accessor = body_index_accessor(document)
    offset = body_index_offset(document, accessor)
    for triangle in covered_body_triangles():
        triangle_offset = 8 + offset + triangle * 6
        indices = struct.unpack_from("<HHH", remaining_chunks, triangle_offset)
        assert indices[0] == indices[1] == indices[2], triangle
    return EXPECTED_COVERED_BODY_TRIANGLES


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

    bust = bust_springs(document)
    tuned_bust_joints = 0
    for spring in bust:
        spring["name"] = "Bust"
        for joint in spring["joints"]:
            node_name = nodes[joint["node"]].get("name", "")
            if node_name.endswith("_end"):
                continue
            joint["stiffness"] = BUST_STIFFNESS
            joint["dragForce"] = BUST_DRAG
            joint["gravityPower"] = BUST_GRAVITY
            joint["gravityDir"] = [0.0, -1.0, 0.0]
            tuned_bust_joints += 1
    return len(springs), tuned_joints, len(bust), tuned_bust_joints


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

    bust = bust_springs(document)
    assert len(bust) == EXPECTED_BUST_SPRINGS, len(bust)
    tuned_bust_joints = 0
    for spring in bust:
        assert spring.get("name") == "Bust"
        for joint in spring["joints"]:
            node_name = nodes[joint["node"]].get("name", "")
            if node_name.endswith("_end"):
                continue
            assert joint.get("stiffness") == BUST_STIFFNESS, node_name
            assert joint.get("dragForce") == BUST_DRAG, node_name
            assert joint.get("gravityPower") == BUST_GRAVITY, node_name
            assert joint.get("gravityDir") == [0.0, -1.0, 0.0], node_name
            tuned_bust_joints += 1
    assert tuned_bust_joints == EXPECTED_BUST_JOINTS, tuned_bust_joints
    return len(springs), tuned_joints, len(bust), tuned_bust_joints


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
        spring_count, joint_count, bust_count, bust_joint_count = apply_profile(document)
        if (
            spring_count != EXPECTED_COAT_SPRINGS
            or joint_count != EXPECTED_COAT_JOINTS
            or bust_count != EXPECTED_BUST_SPRINGS
            or bust_joint_count != EXPECTED_BUST_JOINTS
        ):
            raise AssertionError(
                "unexpected secondary-motion inventory: "
                f"coat={spring_count}/{joint_count}, "
                f"bust={bust_count}/{bust_joint_count}"
            )
        remaining_chunks, masked_triangle_count = mask_covered_body(
            document,
            remaining_chunks,
        )
        if masked_triangle_count != EXPECTED_COVERED_BODY_TRIANGLES:
            raise AssertionError(
                f"unexpected covered body triangle count: {masked_triangle_count}"
            )
        write_glb(VRM_PATH, document, remaining_chunks)
        document, remaining_chunks = read_glb(VRM_PATH)

    spring_count, joint_count, bust_count, bust_joint_count = verify_profile(document)
    masked_triangle_count = verify_body_mask(document, remaining_chunks)
    print(
        "Neon secondary motion OK: "
        f"coat={spring_count} springs/{joint_count} joints, "
        f"bust={bust_count} springs/{bust_joint_count} joints, "
        f"covered body={masked_triangle_count} triangles"
    )


if __name__ == "__main__":
    main()
