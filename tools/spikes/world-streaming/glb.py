"""A minimal glTF 2.0 binary (.glb) writer and reader for the spike's tiles.

One mesh, one triangle primitive: POSITION and NORMAL as float32 VEC3, COLOR_0 as normalized uint8 VEC4,
and uint16 indices (uint32 past 65535 vertices). One opaque, fully rough material, so the renderer's own
lighting decides the look. The output is byte-identical for identical input: no timestamps, sorted keys.
"""

import json
import struct

import numpy as np

UNSIGNED_BYTE = 5121
UNSIGNED_SHORT = 5123
UNSIGNED_INT = 5125
FLOAT = 5126
ARRAY_BUFFER = 34962
ELEMENT_ARRAY_BUFFER = 34963
GENERATOR = "FerrySim world-streaming spike"


def _pad(data, fill):
	return data + fill * ((4 - len(data) % 4) % 4)


def encode(positions, normals, colors, indices):
	positions = np.ascontiguousarray(positions, dtype="<f4")
	normals = np.ascontiguousarray(normals, dtype="<f4")
	colors = np.ascontiguousarray(colors, dtype=np.uint8)
	count = len(positions)
	if normals.shape != (count, 3) or colors.shape != (count, 4) or positions.shape != (count, 3):
		raise ValueError("positions, normals and colours must have one row per vertex")
	index_type = UNSIGNED_SHORT if count <= 65535 else UNSIGNED_INT
	indices = np.ascontiguousarray(indices, dtype="<u2" if index_type == UNSIGNED_SHORT else "<u4")
	if len(indices) % 3 or (len(indices) and int(indices.max()) >= count):
		raise ValueError("indices must be whole triangles referring to existing vertices")

	blobs = [positions.tobytes(), normals.tobytes(), colors.tobytes(), indices.tobytes()]
	views, offset, binary = [], 0, b""
	for k, blob in enumerate(blobs):
		views.append({"buffer": 0, "byteOffset": offset, "byteLength": len(blob),
			"target": ELEMENT_ARRAY_BUFFER if k == 3 else ARRAY_BUFFER})
		binary += _pad(blob, b"\x00")
		offset = len(binary)

	accessors = [
		{"bufferView": 0, "componentType": FLOAT, "count": count, "type": "VEC3",
			"min": [float(v) for v in positions.min(axis=0)], "max": [float(v) for v in positions.max(axis=0)]},
		{"bufferView": 1, "componentType": FLOAT, "count": count, "type": "VEC3"},
		{"bufferView": 2, "componentType": UNSIGNED_BYTE, "normalized": True, "count": count, "type": "VEC4"},
		{"bufferView": 3, "componentType": index_type, "count": len(indices), "type": "SCALAR"},
	]
	document = {
		"asset": {"version": "2.0", "generator": GENERATOR},
		"scene": 0,
		"scenes": [{"nodes": [0]}],
		"nodes": [{"mesh": 0}],
		"meshes": [{"primitives": [{"attributes": {"POSITION": 0, "NORMAL": 1, "COLOR_0": 2}, "indices": 3, "material": 0, "mode": 4}]}],
		"materials": [{"pbrMetallicRoughness": {"baseColorFactor": [1.0, 1.0, 1.0, 1.0], "metallicFactor": 0.0, "roughnessFactor": 1.0}}],
		"accessors": accessors,
		"bufferViews": views,
		"buffers": [{"byteLength": len(binary)}],
	}
	text = _pad(json.dumps(document, sort_keys=True, separators=(",", ":")).encode("utf-8"), b" ")
	total = 12 + 8 + len(text) + 8 + len(binary)
	return (struct.pack("<4sII", b"glTF", 2, total) + struct.pack("<I4s", len(text), b"JSON") + text
		+ struct.pack("<I4s", len(binary), b"BIN\x00") + binary)


_DTYPES = {(FLOAT, "VEC3"): ("<f4", 3), (UNSIGNED_BYTE, "VEC4"): (np.uint8, 4),
	(UNSIGNED_SHORT, "SCALAR"): ("<u2", 1), (UNSIGNED_INT, "SCALAR"): ("<u4", 1)}


def decode(data):
	"""(document, {attribute or 'indices': array}) for files this module writes."""
	magic, version, total = struct.unpack_from("<4sII", data, 0)
	if magic != b"glTF" or version != 2 or total != len(data):
		raise ValueError("not a glTF 2.0 binary of the stated length")
	json_length, json_type = struct.unpack_from("<I4s", data, 12)
	document = json.loads(data[20:20 + json_length].decode("utf-8"))
	bin_length, bin_type = struct.unpack_from("<I4s", data, 20 + json_length)
	if json_type != b"JSON" or bin_type != b"BIN\x00":
		raise ValueError("unexpected chunk types")
	binary = data[28 + json_length:28 + json_length + bin_length]

	def read(index):
		accessor = document["accessors"][index]
		view = document["bufferViews"][accessor["bufferView"]]
		dtype, width = _DTYPES[(accessor["componentType"], accessor["type"])]
		array = np.frombuffer(binary, dtype=dtype, count=accessor["count"] * width, offset=view["byteOffset"])
		return array.reshape(-1, width) if width > 1 else array

	primitive = document["meshes"][0]["primitives"][0]
	mesh = {name: read(index) for name, index in primitive["attributes"].items()}
	mesh["indices"] = read(primitive["indices"])
	return document, mesh
