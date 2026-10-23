"""Committed manifests (data/manifests/<source>.json): provenance, checksums and licence for raw downloads."""

import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

from ferrydata import paths


def sha256_file(path) -> str:
	digest = hashlib.sha256()
	with open(path, "rb") as handle:
		for chunk in iter(lambda: handle.read(1 << 20), b""):
			digest.update(chunk)
	return digest.hexdigest()


def utc_now() -> str:
	return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


class Manifest:
	def __init__(self, source, path, header, files):
		self.source = source
		self.path = Path(path)
		self.header = header
		self.files = files

	@classmethod
	def load(cls, source, directory=None):
		directory = Path(directory) if directory else paths.MANIFEST_DIR
		path = directory / f"{source}.json"
		header = {}
		files = {}
		if path.exists():
			data = json.loads(path.read_text(encoding="utf-8"))
			header = {k: v for k, v in data.items() if k != "files"}
			files = {entry["name"]: entry for entry in data.get("files", [])}
		return cls(source, path, header, files)

	def describe(self, **fields):
		"""Set descriptive header fields (description, license, terms_url, homepage, notes...)."""
		self.header["source"] = self.source
		self.header.update(fields)

	def record(self, name, url, sha256, size_bytes, retrieved_utc=None, **extra):
		entry = {
			"name": name,
			"url": url,
			"retrieved_utc": retrieved_utc or utc_now(),
			"sha256": sha256,
			"size_bytes": size_bytes,
		}
		entry.update(extra)
		self.files[name] = entry

	def verify(self, name, path) -> bool:
		"""True when the file at `path` exists and matches the recorded size and sha256."""
		entry = self.files.get(name)
		path = Path(path)
		if entry is None or not path.is_file():
			return False
		if path.stat().st_size != entry["size_bytes"]:
			return False
		return sha256_file(path) == entry["sha256"]

	def save(self):
		self.path.parent.mkdir(parents=True, exist_ok=True)
		ordered = dict(self.header)
		ordered["files"] = [self.files[name] for name in sorted(self.files)]
		self.path.write_text(json.dumps(ordered, indent="\t", ensure_ascii=False) + "\n", encoding="utf-8")
