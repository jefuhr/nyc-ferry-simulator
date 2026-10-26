"""The harbor 3D Tiles' glTF 2.0 binary writer and reader (ferrydata.world3d.glb).

Failure modes guarded:
- a malformed container: magic, version, total length, chunk types and 4-byte chunk alignment;
- accessors whose counts, byte ranges or POSITION min/max disagree with the data (Cesium rejects or misculls those);
- non-finite vertices, or indices that are out of range or not whole triangles, written or read back silently;
- 16-bit indices used past 65535 vertices;
- output that changes between two writes of the same mesh (generated tiles must be reproducible).
"""

import json
import struct

import numpy as np
import pytest

from ferrydata.world3d import glb


def triangle():
	positions = np.array([[0, 0, 0], [1, 0, 0], [0, 0, -1]], dtype=np.float32)
	normals = np.array([[0, 1, 0]] * 3, dtype=np.float32)
	colors = np.array([[255, 0, 0, 255], [0, 255, 0, 255], [0, 0, 255, 255]], dtype=np.uint8)
	indices = np.array([0, 1, 2], dtype=np.uint32)
	return positions, normals, colors, indices


def chunks(data):
	json_length, json_type = struct.unpack_from("<I4s", data, 12)
	bin_length, bin_type = struct.unpack_from("<I4s", data, 20 + json_length)
	return json_length, json_type, bin_length, bin_type


def rewrite_json(data, edit):
	"""The same file with its JSON chunk edited (padded again), for corrupting a valid file on purpose."""
	json_length = struct.unpack_from("<I", data, 12)[0]
	document = json.loads(data[20:20 + json_length])
	edit(document)
	text = json.dumps(document, separators=(",", ":")).encode()
	text += b" " * ((4 - len(text) % 4) % 4)
	rest = data[20 + json_length:]
	total = 12 + 8 + len(text) + len(rest)
	return struct.pack("<4sII", b"glTF", 2, total) + struct.pack("<I4s", len(text), b"JSON") + text + rest


def test_container_layout():
	data = glb.encode(*triangle())
	magic, version, length = struct.unpack_from("<4sII", data, 0)
	assert magic == b"glTF" and version == 2 and length == len(data)
	json_length, json_type, bin_length, bin_type = chunks(data)
	assert json_type == b"JSON" and json_length % 4 == 0
	assert bin_type == b"BIN\x00" and bin_length % 4 == 0
	assert 20 + json_length + 8 + bin_length == len(data)


def test_round_trip_and_accessors():
	positions, normals, colors, indices = triangle()
	document, mesh = glb.decode(glb.encode(positions, normals, colors, indices))
	assert np.array_equal(mesh["POSITION"], positions)
	assert np.array_equal(mesh["NORMAL"], normals)
	assert np.array_equal(mesh["COLOR_0"], colors)
	assert np.array_equal(mesh["indices"], indices)
	primitive = document["meshes"][0]["primitives"][0]
	position = document["accessors"][primitive["attributes"]["POSITION"]]
	assert position["min"] == [0.0, 0.0, -1.0] and position["max"] == [1.0, 0.0, 0.0]
	for view in document["bufferViews"]:
		assert view["byteOffset"] % 4 == 0
		assert view["byteOffset"] + view["byteLength"] <= document["buffers"][0]["byteLength"]
	assert document["accessors"][primitive["indices"]]["componentType"] == glb.UNSIGNED_SHORT
	assert document["accessors"][primitive["attributes"]["COLOR_0"]]["normalized"] is True
	assert primitive["mode"] == 4


def test_large_meshes_use_32_bit_indices():
	count = 70000
	positions = np.zeros((count, 3), dtype=np.float32)
	positions[:, 0] = np.arange(count)
	normals = np.tile(np.array([0, 1, 0], dtype=np.float32), (count, 1))
	colors = np.full((count, 4), 255, dtype=np.uint8)
	indices = np.array([0, 1, count - 1], dtype=np.uint32)
	document, mesh = glb.decode(glb.encode(positions, normals, colors, indices))
	primitive = document["meshes"][0]["primitives"][0]
	assert document["accessors"][primitive["indices"]]["componentType"] == glb.UNSIGNED_INT
	assert mesh["indices"][-1] == count - 1


def test_identical_bytes_on_rewrite():
	assert glb.encode(*triangle()) == glb.encode(*triangle())


def test_json_is_plain_gltf_2_without_extensions():
	data = glb.encode(*triangle())
	json_length = struct.unpack_from("<I", data, 12)[0]
	document = json.loads(data[20:20 + json_length].decode("utf-8"))
	assert document["asset"]["version"] == "2.0"
	assert document["scene"] == 0 and document["scenes"][0]["nodes"] == [0]
	assert "extensionsUsed" not in document and "extensionsRequired" not in document
	assert "matrix" not in document["nodes"][0]


@pytest.mark.parametrize("change", [
	lambda p, n, c, i: (p.__setitem__((1, 0), np.nan), "finite"),
	lambda p, n, c, i: (p.__setitem__((2, 2), np.inf), "finite"),
	lambda p, n, c, i: (i.__setitem__(2, 3), "vertices"),
])
def test_writer_refuses_bad_meshes(change):
	positions, normals, colors, indices = triangle()
	_, message = change(positions, normals, colors, indices)
	with pytest.raises(ValueError, match=message):
		glb.encode(positions, normals, colors, indices)


def test_writer_refuses_partial_triangles_and_mismatched_attributes():
	positions, normals, colors, indices = triangle()
	with pytest.raises(ValueError, match="whole triangles"):
		glb.encode(positions, normals, colors, indices[:2])
	with pytest.raises(ValueError, match="one row per vertex"):
		glb.encode(positions, normals[:2], colors, indices)
	with pytest.raises(ValueError, match="no triangles"):
		glb.encode(positions, normals, colors, indices[:0])


@pytest.mark.parametrize("corrupt,message", [
	(lambda d: b"glTx" + d[4:], "glTF 2.0"),
	(lambda d: d[:8] + struct.pack("<I", len(d) + 4) + d[12:], "length"),
	(lambda d: d[:-4], "length"),
	(lambda d: d[:16] + b"JSOX" + d[20:], "chunk"),
])
def test_reader_refuses_malformed_containers(corrupt, message):
	with pytest.raises(glb.GlbError, match=message):
		glb.decode(corrupt(glb.encode(*triangle())))


def test_reader_refuses_misaligned_chunks():
	data = glb.encode(*triangle())
	json_length = struct.unpack_from("<I", data, 12)[0]
	# Declare the JSON chunk three bytes shorter: still valid JSON with the trailing padding, but misaligned.
	broken = data[:12] + struct.pack("<I", json_length - 3) + data[16:]
	with pytest.raises(glb.GlbError):
		glb.decode(broken)


@pytest.mark.parametrize("edit,message", [
	(lambda doc: doc["accessors"][0].__setitem__("count", 4), "outside its buffer view|count"),
	(lambda doc: doc["bufferViews"][0].__setitem__("byteLength", 10 ** 6), "outside the binary chunk"),
	(lambda doc: doc["accessors"][0].__setitem__("max", [9.0, 0.0, 0.0]), "min/max"),
	(lambda doc: doc["accessors"][0].pop("min"), "min/max"),
	(lambda doc: doc["meshes"][0]["primitives"][0].__setitem__("mode", 1), "triangles"),
	(lambda doc: doc["accessors"][3].__setitem__("count", 2), "whole triangles"),
])
def test_reader_refuses_inconsistent_accessors(edit, message):
	data = rewrite_json(glb.encode(*triangle()), edit)
	with pytest.raises(glb.GlbError, match=message):
		glb.decode(data)


def test_reader_refuses_bad_values_in_the_binary_chunk():
	positions, normals, colors, indices = triangle()
	data = bytearray(glb.encode(positions, normals, colors, indices))
	json_length = struct.unpack_from("<I", data, 12)[0]
	binary = 28 + json_length
	nan = bytearray(data)
	nan[binary:binary + 4] = struct.pack("<f", float("nan"))
	with pytest.raises(glb.GlbError, match="finite"):
		glb.decode(bytes(nan))
	document = json.loads(bytes(data[20:20 + json_length]))
	view = document["bufferViews"][document["accessors"][3]["bufferView"]]
	out_of_range = bytearray(data)
	out_of_range[binary + view["byteOffset"]:binary + view["byteOffset"] + 2] = struct.pack("<H", 7)
	with pytest.raises(glb.GlbError, match="index"):
		glb.decode(bytes(out_of_range))
