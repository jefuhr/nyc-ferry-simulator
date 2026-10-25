"""The spike's minimal glTF 2.0 binary writer.

Failure modes guarded:
- a malformed container (magic, version, total length, chunk types, 4-byte chunk alignment);
- accessors whose counts, byte ranges or POSITION min/max disagree with the data (Cesium rejects or misculls those);
- 16-bit indices used past 65535 vertices;
- output that changes between two writes of the same mesh (generated tiles must be reproducible).
"""

import json
import struct

import numpy as np

import glb


def triangle():
	positions = np.array([[0, 0, 0], [1, 0, 0], [0, 0, -1]], dtype=np.float32)
	normals = np.array([[0, 1, 0]] * 3, dtype=np.float32)
	colors = np.array([[255, 0, 0, 255], [0, 255, 0, 255], [0, 0, 255, 255]], dtype=np.uint8)
	indices = np.array([0, 1, 2], dtype=np.uint32)
	return positions, normals, colors, indices


def test_container_layout():
	data = glb.encode(*triangle())
	magic, version, length = struct.unpack_from("<4sII", data, 0)
	assert magic == b"glTF" and version == 2 and length == len(data)
	json_length, json_type = struct.unpack_from("<I4s", data, 12)
	assert json_type == b"JSON" and json_length % 4 == 0
	bin_length, bin_type = struct.unpack_from("<I4s", data, 20 + json_length)
	assert bin_type == b"BIN\x00" and bin_length % 4 == 0
	assert 20 + json_length + 8 + bin_length == len(data)


def test_round_trip_and_accessors():
	positions, normals, colors, indices = triangle()
	document, mesh = glb.decode(glb.encode(positions, normals, colors, indices))
	assert np.array_equal(mesh["POSITION"], positions)
	assert np.array_equal(mesh["NORMAL"], normals)
	assert np.array_equal(mesh["COLOR_0"], colors)
	assert np.array_equal(mesh["indices"], indices)
	position = document["accessors"][document["meshes"][0]["primitives"][0]["attributes"]["POSITION"]]
	assert position["min"] == [0.0, 0.0, -1.0] and position["max"] == [1.0, 0.0, 0.0]
	for view in document["bufferViews"]:
		assert view["byteOffset"] % 4 == 0
		assert view["byteOffset"] + view["byteLength"] <= document["buffers"][0]["byteLength"]
	assert document["accessors"][document["meshes"][0]["primitives"][0]["indices"]]["componentType"] == glb.UNSIGNED_SHORT


def test_large_meshes_use_32_bit_indices():
	count = 70000
	positions = np.zeros((count, 3), dtype=np.float32)
	positions[:, 0] = np.arange(count)
	normals = np.tile(np.array([0, 1, 0], dtype=np.float32), (count, 1))
	colors = np.full((count, 4), 255, dtype=np.uint8)
	indices = np.array([0, 1, count - 1], dtype=np.uint32)
	document, mesh = glb.decode(glb.encode(positions, normals, colors, indices))
	assert document["accessors"][document["meshes"][0]["primitives"][0]["indices"]]["componentType"] == glb.UNSIGNED_INT
	assert mesh["indices"][-1] == count - 1


def test_identical_bytes_on_rewrite():
	assert glb.encode(*triangle()) == glb.encode(*triangle())


def test_json_is_plain_gltf_2():
	data = glb.encode(*triangle())
	json_length = struct.unpack_from("<I", data, 12)[0]
	document = json.loads(data[20:20 + json_length].decode("utf-8"))
	assert document["asset"]["version"] == "2.0"
	assert document["scene"] == 0 and document["scenes"][0]["nodes"] == [0]
	assert "extensionsUsed" not in document
