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
EXPECTED_BUST_CLOTHING_VERTICES = 356
BUST_CLOTHING_TRANSFER_DISTANCE = 0.06
BUST_CLOTHING_TRANSFER_MIN_WEIGHT = 0.02

COMPONENT_FORMATS = {
    5121: ("B", 1),
    5126: ("f", 4),
}
TYPE_COMPONENTS = {
    "VEC3": 3,
    "VEC4": 4,
}


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
    return document, bytearray(raw[json_end:])


def binary_chunk(remaining_chunks):
    length, chunk_type = struct.unpack_from("<II", remaining_chunks, 0)
    if chunk_type != 0x004E4942:
        raise ValueError("second GLB chunk is not BIN")
    return memoryview(remaining_chunks)[8:8 + length]


def accessor_layout(document, accessor_index):
    accessor = document["accessors"][accessor_index]
    view = document["bufferViews"][accessor["bufferView"]]
    component_type = accessor["componentType"]
    if component_type not in COMPONENT_FORMATS:
        raise ValueError(f"unsupported component type: {component_type}")
    if accessor["type"] not in TYPE_COMPONENTS:
        raise ValueError(f"unsupported accessor type: {accessor['type']}")
    component_format, component_size = COMPONENT_FORMATS[component_type]
    component_count = TYPE_COMPONENTS[accessor["type"]]
    element_size = component_size * component_count
    return (
        accessor,
        component_format,
        component_count,
        view.get("byteOffset", 0) + accessor.get("byteOffset", 0),
        view.get("byteStride", element_size),
    )


def read_accessor(document, binary, accessor_index):
    accessor, component_format, component_count, offset, stride = (
        accessor_layout(document, accessor_index)
    )
    unpack_format = "<" + component_format * component_count
    return [
        struct.unpack_from(unpack_format, binary, offset + index * stride)
        for index in range(accessor["count"])
    ]


def write_float4_accessor(document, binary, accessor_index, values):
    accessor, component_format, component_count, offset, stride = (
        accessor_layout(document, accessor_index)
    )
    if component_format != "f" or component_count != 4:
        raise ValueError("weight accessor must be a float VEC4")
    if len(values) != accessor["count"]:
        raise ValueError("weight accessor length changed")
    for index, value in enumerate(values):
        struct.pack_into("<ffff", binary, offset + index * stride, *value)


def write_ubyte4_accessor(document, binary, accessor_index, values):
    accessor, component_format, component_count, offset, stride = (
        accessor_layout(document, accessor_index)
    )
    if component_format != "B" or component_count != 4:
        raise ValueError("joint accessor must be an unsigned-byte VEC4")
    if len(values) != accessor["count"]:
        raise ValueError("joint accessor length changed")
    for index, value in enumerate(values):
        struct.pack_into("<BBBB", binary, offset + index * stride, *value)


def material_name(document, primitive):
    material_index = primitive.get("material")
    if material_index is None:
        return ""
    return document["materials"][material_index].get("name", "")


def squared_distance(left, right):
    return sum((left[axis] - right[axis]) ** 2 for axis in range(3))


def bust_weight_data(document, binary):
    nodes = document["nodes"]
    bust_nodes = {
        index
        for index, node in enumerate(nodes)
        if "Bust" in node.get("name", "")
        and not node.get("name", "").endswith("_end")
    }
    for node in nodes:
        if "mesh" not in node or "skin" not in node:
            continue
        mesh = document["meshes"][node["mesh"]]
        body = None
        for primitive in mesh["primitives"]:
            if "Body_00_SKIN" in material_name(document, primitive):
                body = primitive
                break
        if body is None:
            continue

        skin_joints = document["skins"][node["skin"]]["joints"]

        def primitive_data(primitive):
            attributes = primitive["attributes"]
            positions = read_accessor(document, binary, attributes["POSITION"])
            joints = read_accessor(document, binary, attributes["JOINTS_0"])
            weights = read_accessor(document, binary, attributes["WEIGHTS_0"])
            totals = [
                sum(
                    weight
                    for slot, weight in zip(vertex_joints, vertex_weights)
                    if skin_joints[slot] in bust_nodes
                )
                for vertex_joints, vertex_weights in zip(joints, weights)
            ]
            return positions, joints, weights, totals

        body_positions, body_joints, body_weights, body_totals = (
            primitive_data(body)
        )
        body_bust_weights = []
        for joints, weights in zip(body_joints, body_weights):
            body_bust_weights.append({
                slot: weight
                for slot, weight in zip(joints, weights)
                if skin_joints[slot] in bust_nodes and weight > 0.0
            })
        clothing = []
        for primitive in mesh["primitives"]:
            name = material_name(document, primitive)
            if "Tops" not in name or "CLOTH" not in name:
                continue
            clothing.append((primitive, primitive_data(primitive)))
        return (
            skin_joints,
            bust_nodes,
            body_positions,
            body_totals,
            body_bust_weights,
            clothing,
        )
    raise AssertionError("Neon skinned body mesh was not found")


def nearest_body_index(position, body_positions):
    return min(
        range(len(body_positions)),
        key=lambda index: squared_distance(position, body_positions[index]),
    )


def nearest_body_total(position, body_positions, body_totals):
    return body_totals[nearest_body_index(position, body_positions)]


def bust_transfer_bounds(body_positions, body_totals):
    weighted = [
        position
        for position, total in zip(body_positions, body_totals)
        if total >= BUST_CLOTHING_TRANSFER_MIN_WEIGHT
    ]
    return tuple(
        (min(position[axis] for position in weighted),
         max(position[axis] for position in weighted))
        for axis in range(3)
    )


def inside_transfer_bounds(position, bounds):
    return all(
        lower - BUST_CLOTHING_TRANSFER_DISTANCE <= position[axis]
        <= upper + BUST_CLOTHING_TRANSFER_DISTANCE
        for axis, (lower, upper) in enumerate(bounds)
    )


def apply_bust_clothing_weights(document, remaining_chunks):
    binary = binary_chunk(remaining_chunks)
    (
        skin_joints,
        bust_nodes,
        body_positions,
        body_totals,
        body_bust_weights,
        clothing,
    ) = bust_weight_data(document, binary)
    transfer_bounds = bust_transfer_bounds(body_positions, body_totals)
    weighted_vertices = 0
    changed_vertices = 0
    for primitive, data in clothing:
        positions, joints, weights, totals = data
        changed = False
        mutable_joints = [list(vertex) for vertex in joints]
        mutable_weights = [list(vertex) for vertex in weights]
        for vertex_index, current_total in enumerate(totals):
            if (
                current_total <= 0.000001
                and not inside_transfer_bounds(
                    positions[vertex_index], transfer_bounds
                )
            ):
                continue
            body_index = nearest_body_index(
                positions[vertex_index], body_positions
            )
            target_total = body_totals[body_index]
            distance = squared_distance(
                positions[vertex_index], body_positions[body_index]
            ) ** 0.5
            should_add_bust = (
                current_total <= 0.000001
                and target_total >= BUST_CLOTHING_TRANSFER_MIN_WEIGHT
                and distance <= BUST_CLOTHING_TRANSFER_DISTANCE
            )
            if current_total <= 0.000001 and not should_add_bust:
                continue
            weighted_vertices += 1

            current = {}
            for slot, weight in zip(
                joints[vertex_index], weights[vertex_index]
            ):
                current[slot] = current.get(slot, 0.0) + weight

            desired_bust = {
                slot: weight
                for slot, weight in current.items()
                if skin_joints[slot] in bust_nodes and weight > 0.0
            }
            for slot, weight in body_bust_weights[body_index].items():
                current_weight = desired_bust.get(slot, 0.0)
                if weight > current_weight + 0.000001:
                    desired_bust[slot] = weight
            desired_total = sum(desired_bust.values())
            if desired_total > 1.0:
                # At the bra's center seam the nearest body vertex may belong
                # to the opposite side. Component-wise maxima can then combine
                # both breasts above 1.0 and drift on every repeated run. Use
                # the nearest body's coherent Bust vector for those vertices.
                desired_bust = dict(body_bust_weights[body_index])
                desired_total = sum(desired_bust.values())

            other = {
                slot: weight
                for slot, weight in current.items()
                if skin_joints[slot] not in bust_nodes and weight > 0.0
            }
            other_total = sum(other.values())
            if other_total > 0.0:
                other_scale = (1.0 - desired_total) / other_total
                other = {
                    slot: weight * other_scale
                    for slot, weight in other.items()
                }

            bust_items = sorted(
                desired_bust.items(),
                key=lambda item: (-item[1], item[0]),
            )[:4]
            remaining_slots = 4 - len(bust_items)
            other_items = sorted(
                other.items(),
                key=lambda item: (-item[1], item[0]),
            )[:remaining_slots]
            strongest = bust_items + other_items
            while len(strongest) < 4:
                strongest.append((strongest[0][0], 0.0))
            total = sum(weight for _, weight in strongest)
            new_joints = [slot for slot, _ in strongest]
            new_weights = [weight / total for _, weight in strongest]
            if (
                new_joints == list(joints[vertex_index])
                and all(
                    abs(left - right) <= 0.000001
                    for left, right in zip(
                        new_weights, weights[vertex_index]
                    )
                )
            ):
                continue
            mutable_joints[vertex_index] = new_joints
            mutable_weights[vertex_index] = new_weights
            changed_vertices += 1
            changed = True
        if changed:
            write_ubyte4_accessor(
                document,
                binary,
                primitive["attributes"]["JOINTS_0"],
                mutable_joints,
            )
            write_float4_accessor(
                document,
                binary,
                primitive["attributes"]["WEIGHTS_0"],
                mutable_weights,
            )
    return weighted_vertices, changed_vertices


def verify_bust_clothing_weights(document, remaining_chunks):
    binary = binary_chunk(remaining_chunks)
    (
        _,
        _,
        body_positions,
        body_totals,
        _,
        clothing,
    ) = bust_weight_data(document, binary)
    transfer_bounds = bust_transfer_bounds(body_positions, body_totals)
    weighted_vertices = 0
    for _, data in clothing:
        positions, _, _, totals = data
        for position, current_total in zip(positions, totals):
            if (
                current_total <= 0.000001
                and not inside_transfer_bounds(position, transfer_bounds)
            ):
                continue
            body_index = nearest_body_index(position, body_positions)
            target_total = body_totals[body_index]
            distance = squared_distance(
                position, body_positions[body_index]
            ) ** 0.5
            should_have_bust = (
                target_total >= BUST_CLOTHING_TRANSFER_MIN_WEIGHT
                and distance <= BUST_CLOTHING_TRANSFER_DISTANCE
            )
            if current_total <= 0.000001 and not should_have_bust:
                continue
            weighted_vertices += 1
            assert current_total + 0.00001 >= target_total, (
                current_total,
                target_total,
            )
    assert weighted_vertices == EXPECTED_BUST_CLOTHING_VERTICES, weighted_vertices
    return weighted_vertices


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
        weighted_vertices, changed_vertices = apply_bust_clothing_weights(
            document, remaining_chunks
        )
        if (
            spring_count != EXPECTED_COAT_SPRINGS
            or joint_count != EXPECTED_COAT_JOINTS
            or bust_count != EXPECTED_BUST_SPRINGS
            or bust_joint_count != EXPECTED_BUST_JOINTS
            or weighted_vertices != EXPECTED_BUST_CLOTHING_VERTICES
        ):
            raise AssertionError(
                "unexpected secondary-motion inventory: "
                f"coat={spring_count}/{joint_count}, "
                f"bust={bust_count}/{bust_joint_count}, "
                f"bust clothing vertices={weighted_vertices}"
            )
        write_glb(VRM_PATH, document, remaining_chunks)
        document, remaining_chunks = read_glb(VRM_PATH)
        print(f"Adjusted bust clothing weights on {changed_vertices} vertices")

    spring_count, joint_count, bust_count, bust_joint_count = verify_profile(document)
    weighted_vertices = verify_bust_clothing_weights(document, remaining_chunks)
    print(
        "Neon secondary motion OK: "
        f"coat={spring_count} springs/{joint_count} joints, "
        f"bust={bust_count} springs/{bust_joint_count} joints, "
        f"bust clothing={weighted_vertices} vertices"
    )


if __name__ == "__main__":
    main()
