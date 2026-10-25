"""Idempotent, checksum-verified downloads into the raw cache (stdlib urllib only)."""

import os
import time
import urllib.error
import urllib.request
from pathlib import Path

from ferrydata import manifest as manifest_module

USER_AGENT = "ferrydata/0.1 (NYC Ferry Simulator data pipeline; research)"
_CHUNK = 1 << 20


class ChecksumError(RuntimeError):
	pass


class RequestRejected(ValueError):
	"""The server answered with a 4xx status; retrying will not help."""


def restore_pinned_file(manifest, name, dest):
	"""Restore missing bytes using an existing pin without rewriting its provenance."""
	dest = Path(dest)
	entry = manifest.files[name]
	if dest.exists():
		if manifest.verify(name, dest):
			return "cached"
		raise ChecksumError(f"refusing to replace existing {dest}")
	dest.parent.mkdir(parents=True, exist_ok=True)
	part = dest.with_name(dest.name + ".part")
	try:
		size = _download(entry["url"], part)
		if size != entry["size_bytes"] or manifest_module.sha256_file(part) != entry["sha256"]:
			raise ChecksumError(f"'{name}' changed upstream; original pin is unchanged")
		os.replace(part, dest)
	finally:
		if part.exists():
			part.unlink()
	return "downloaded"


def http_get(url, timeout=120, retries=3):
	"""Return the body of a URL as bytes (for small API responses)."""
	last = None
	for attempt in range(retries):
		try:
			request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
			with urllib.request.urlopen(request, timeout=timeout) as response:
				return response.read()
		except (urllib.error.URLError, TimeoutError, ConnectionError) as error:
			last = error
			time.sleep(2 * (attempt + 1))
	raise RuntimeError(f"GET {url} failed after {retries} attempts: {last}")


def _download(url, part_path, timeout=120, retries=3):
	last = None
	for attempt in range(retries):
		try:
			request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
			with urllib.request.urlopen(request, timeout=timeout) as response:
				expected = response.headers.get("Content-Length")
				written = 0
				with open(part_path, "wb") as out:
					while True:
						chunk = response.read(_CHUNK)
						if not chunk:
							break
						out.write(chunk)
						written += len(chunk)
			if expected is not None and written != int(expected):
				raise OSError(f"short read: got {written} of {expected} bytes")
			return written
		except urllib.error.HTTPError as error:
			if 400 <= error.code < 500:
				body = error.read(300).decode("utf-8", errors="replace").strip()
				raise RequestRejected(f"HTTP {error.code} from {url}: {body}") from error
			last = error
			time.sleep(2 * (attempt + 1))
		except (urllib.error.URLError, TimeoutError, ConnectionError, OSError) as error:
			last = error
			time.sleep(2 * (attempt + 1))
	raise RuntimeError(f"download of {url} failed after {retries} attempts: {last}")


def fetch_file(manifest, name, url, dest, force=False, pinned=True, extra=None, validate=None):
	"""Make sure `dest` holds `url`, verified against the manifest. Returns 'cached' or 'downloaded'.

	- cache hit that matches the manifest: no network, manifest untouched
	- cache present but not matching the manifest: ChecksumError unless force
	- fresh download whose sha256 differs from the manifest: ChecksumError unless force or pinned=False
	  (pinned=False is for floating catalogues such as station lists)
	- validate(path), when given, may raise ValueError to reject a download (APIs that answer 200 with an error text)
	"""
	dest = Path(dest)
	entry = manifest.files.get(name)
	if dest.exists() and not force:
		if manifest.verify(name, dest):
			return "cached"
		if entry is not None and pinned:
			raise ChecksumError(f"{dest} does not match the manifest entry for '{name}'; re-run fetch with --force to replace it")

	dest.parent.mkdir(parents=True, exist_ok=True)
	part = dest.with_name(dest.name + ".part")
	try:
		size = _download(url, part)
		if validate is not None:
			validate(part)
		digest = manifest_module.sha256_file(part)
		if entry is not None and pinned and not force and digest != entry["sha256"]:
			raise ChecksumError(
				f"'{name}' changed upstream (sha256 {digest[:12]}... vs manifest {entry['sha256'][:12]}...); re-run fetch with --force to accept it"
			)
		os.replace(part, dest)
	finally:
		if part.exists():
			part.unlink()
	manifest.record(name, url, digest, size, **(extra or {}))
	manifest.save()
	return "downloaded"
