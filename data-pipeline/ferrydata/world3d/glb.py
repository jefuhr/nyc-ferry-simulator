"""A minimal glTF 2.0 binary (.glb) writer and a strict reader for the harbor's 3D Tiles (numpy only).

Hardened from the world-streaming spike's writer (tools/spikes/world-streaming/glb.py). One mesh with one triangle
primitive: POSITION and NORMAL as float32 VEC3, COLOR_0 as normalized uint8 VEC4, and uint16 indices (uint32 past
65535 vertices). One opaque, fully rough material, so the renderer's own lighting decides the look. No extensions.
The output is byte-identical for identical input: no timestamps, sorted keys, fixed padding.

The reader accepts exactly what the writer produces and checks everything a consumer relies on: the container, every
accessor's byte range and count, POSITION's min/max against the data, finite values, and whole triangles with indices
in range. It raises GlbError naming the first problem.
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
TRIANGLES = 4
GENERATOR = "FerrySim ferrydata.world3d"

_COMPONENTS = {(FLOAT, "VEC3"): ("<f4", 3, 12), (UNSIGNED_BYTE, "VEC4"): (np.uint8, 4, 4),
	(UNSIGNED_SHORT, "SCALAR"): ("<u2", 1, 2), (UNSIGNED_INT, "SCALAR"): ("<u4", 1, 4)}
_ATTRIBUTES = {"POSITION": (FLOAT, "VEC3"), "NORMAL": (FLOAT, "VEC3"), "COLOR_0": (UNSIGNED_BYTE, "VEC4")}


class GlbError(ValueError):
	"""A .glb that this module did not write, or one that was damaged."""


def _pad(data, fill):
	return data + fill * ((4 - len(data) % 4) % 4)


def encode(positions, normals, colors, indices, generator=GENERATOR):
	positions = np.ascontiguousarray(positions, dtype="<f4")
	normals = np.ascontiguousarray(normals, dtype="<f4")
	colors = np.ascontiguousarray(colors, dtype=np.uint8)
	count = len(positions)
	if positions.shape != (count, 3) or normals.shape != (count, 3) or colors.shape != (count, 4):
		raise ValueError("positions, normals and colours must have one row per vertex")
	if not (np.isfinite(positions).all() and np.isfinite(normals).all()):
		raise ValueError("positions and normals must be finite")
	indices = np.asarray(indices)
	if len(indices) == 0:
		raise ValueError("a mesh with no triangles has nothing to draw")
	if len(indices) % 3:
		raise ValueError("indices must be whole triangles")
	if int(indices.min()) < 0 or int(indices.max()) >= count:
		raise ValueError("indices must refer to existing vertices")
	index_type = UNSIGNED_SHORT if count <= 65535 else UNSIGNED_INT
	indices = np.ascontiguousarray(indices, dtype="<u2" if index_type == UNSIGNED_SHORT else "<u4")

	blobs = [positions.tobytes(), normals.tobytes(), colors.tobytes(), indices.tobytes()]
	views, binary = [], b""
	for k, blob in enumerate(blobs):
		views.append({"buffer": 0, "byteOffset": len(binary), "byteLength": len(blob),
			"target": ELEMENT_ARRAY_BUFFER if k == 3 else ARRAY_BUFFER})
		binary += _pad(blob, b"\x00")

	accessors = [
		{"bufferView": 0, "componentType": FLOAT, "count": count, "type": "VEC3",
			"min": [float(v) for v in positions.min(axis=0)], "max": [float(v) for v in positions.max(axis=0)]},
		{"bufferView": 1, "componentType": FLOAT, "count": count, "type": "VEC3"},
		{"bufferView": 2, "componentType": UNSIGNED_BYTE, "normalized": True, "count": count, "type": "VEC4"},
		{"bufferView": 3, "componentType": index_type, "count": len(indices), "type": "SCALAR"},
	]
	document = {
		"asset": {"version": "2.0", "generator": generator},
		"scene": 0,
		"scenes": [{"nodes": [0]}],
		"nodes": [{"mesh": 0}],
		"meshes": [{"primitives": [{"attributes": {"POSITION": 0, "NORMAL": 1, "COLOR_0": 2}, "indices": 3, "material": 0, "mode": TRIANGLES}]}],
		"materials": [{"pbrMetallicRoughness": {"baseColorFactor": [1.0, 1.0, 1.0, 1.0], "metallicFactor": 0.0, "roughnessFactor": 1.0}}],
		"accessors": accessors,
		"bufferViews": views,
		"buffers": [{"byteLength": len(binary)}],
	}
	text = _pad(json.dumps(document, sort_keys=True, separators=(",", ":")).encode("utf-8"), b" ")
	total = 12 + 8 + len(text) + 8 + len(binary)
	return (struct.pack("<4sII", b"glTF", 2, total) + struct.pack("<I4s", len(text), b"JSON") + text
		+ struct.pack("<I4s", len(binary), b"BIN\x00") + binary)


def decode(data):
	"""(document, {"POSITION", "NORMAL", "COLOR_0", "indices": array}) of a file this module wrote, checked strictly."""
	data = bytes(data)
	if len(data) < 28:
		raise GlbError("not a glTF 2.0 binary: shorter than its headers")
	magic, version, total = struct.unpack_from("<4sII", data, 0)
	if magic != b"glTF" or version != 2:
		raise GlbError("not a glTF 2.0 binary (magic or version)")
	if total != len(data):
		raise GlbError(f"declared length {total} differs from the file's {len(data)} bytes")
	json_length, json_type = struct.unpack_from("<I4s", data, 12)
	if json_type != b"JSON":
		raise GlbError("the first chunk is not the JSON chunk")
	if json_length % 4 or 20 + json_length + 8 > len(data):
		raise GlbError("the JSON chunk is misaligned or overruns the file")
	try:
		document = json.loads(data[20:20 + json_length].decode("utf-8"))
	except (UnicodeDecodeError, json.JSONDecodeError) as error:
		raise GlbError(f"the JSON chunk does not parse: {error}") from None
	bin_length, bin_type = struct.unpack_from("<I4s", data, 20 + json_length)
	if bin_type != b"BIN\x00":
		raise GlbError("the second chunk is not the binary chunk")
	start = 28 + json_length
	if bin_length % 4 or start + bin_length != len(data):
		raise GlbError("the binary chunk is misaligned or its length disagrees with the file")
	binary = data[start:start + bin_length]
	if document.get("asset", {}).get("version") != "2.0":
		raise GlbError("asset.version is not 2.0")
	buffers = document.get("buffers", [])
	if len(buffers) != 1 or "uri" in buffers[0] or buffers[0].get("byteLength") != bin_length:
		raise GlbError("expected one buffer, the binary chunk, of its exact length")
	meshes = document.get("meshes", [])
	if len(meshes) != 1 or len(meshes[0].get("primitives", [])) != 1:
		raise GlbError("expected one mesh with one primitive")
	primitive = meshes[0]["primitives"][0]
	if primitive.get("mode", TRIANGLES) != TRIANGLES:
		raise GlbError("the primitive does not draw triangles")
	if sorted(primitive.get("attributes", {})) != sorted(_ATTRIBUTES) or "indices" not in primitive:
		raise GlbError("expected POSITION, NORMAL, COLOR_0 and indices")

	def read(index, expected, name):
		try:
			accessor = document["accessors"][index]
			view = document["bufferViews"][accessor["bufferView"]]
		except (IndexError, KeyError, TypeError):
			raise GlbError(f"{name}: missing accessor or buffer view") from None
		key = (accessor.get("componentType"), accessor.get("type"))
		if key not in _COMPONENTS or (expected is not None and key != expected):
			raise GlbError(f"{name}: unexpected component type {key}")
		dtype, width, size = _COMPONENTS[key]
		offset, length = view.get("byteOffset", 0), view.get("byteLength", -1)
		if view.get("buffer") != 0 or offset % 4 or offset < 0 or length < 0 or offset + length > bin_length:
			raise GlbError(f"{name}: buffer view outside the binary chunk")
		count = accessor.get("count", 0)
		if count < 1 or accessor.get("byteOffset", 0) != 0 or count * size > length:
			raise GlbError(f"{name}: count {count} lies outside its buffer view")
		if name == "COLOR_0" and accessor.get("normalized") is not True:
			raise GlbError("COLOR_0 must be normalized")
		array = np.frombuffer(binary, dtype=dtype, count=count * width, offset=offset)
		return accessor, (array.reshape(-1, width) if width > 1 else array)

	mesh = {}
	position_accessor = None
	for name, expected in _ATTRIBUTES.items():
		accessor, array = read(primitive["attributes"][name], expected, name)
		mesh[name] = array
		if name == "POSITION":
			position_accessor = accessor
	count = len(mesh["POSITION"])
	if any(len(mesh[name]) != count for name in _ATTRIBUTES):
		raise GlbError("attribute counts differ")
	for name in ("POSITION", "NORMAL"):
		if not np.isfinite(mesh[name]).all():
			raise GlbError(f"{name} has values that are not finite")
	if "min" not in position_accessor or "max" not in position_accessor:
		raise GlbError("POSITION min/max are missing")
	if (position_accessor["min"] != [float(v) for v in mesh["POSITION"].min(axis=0)]
			or position_accessor["max"] != [float(v) for v in mesh["POSITION"].max(axis=0)]):
		raise GlbError("POSITION min/max disagree with the data")
	_, indices = read(primitive["indices"], None, "indices")
	if indices.dtype.itemsize == 2 and count > 65535:
		raise GlbError("16-bit indices for more than 65535 vertices")
	if len(indices) % 3:
		raise GlbError("indices are not whole triangles")
	if int(indices.max()) >= count:
		raise GlbError(f"an index ({int(indices.max())}) refers past the last vertex ({count - 1})")
	mesh["indices"] = indices
	return document, mesh
