import hashlib
import json

import pytest

from ferrydata import fetcher, manifest


def _source(tmp_path, content=b"hello ferry"):
	src = tmp_path / "upstream.bin"
	src.write_bytes(content)
	return src, src.as_uri()


def _manifest(tmp_path):
	return manifest.Manifest.load("unit", directory=tmp_path / "manifests")


def test_sha256_file(tmp_path):
	f = tmp_path / "a.txt"
	f.write_bytes(b"abc")
	assert manifest.sha256_file(f) == hashlib.sha256(b"abc").hexdigest()


def test_fetch_downloads_records_and_saves(tmp_path):
	src, url = _source(tmp_path)
	m = _manifest(tmp_path)
	dest = tmp_path / "cache" / "a.bin"
	status = fetcher.fetch_file(m, "a.bin", url, dest)
	assert status == "downloaded"
	assert dest.read_bytes() == b"hello ferry"
	saved = json.loads((tmp_path / "manifests" / "unit.json").read_text())
	entry = saved["files"][0]
	assert entry["url"] == url
	assert entry["sha256"] == hashlib.sha256(b"hello ferry").hexdigest()
	assert entry["size_bytes"] == 11
	assert entry["retrieved_utc"].endswith("Z")


def test_fetch_is_idempotent_and_keeps_timestamp(tmp_path):
	src, url = _source(tmp_path)
	m = _manifest(tmp_path)
	dest = tmp_path / "cache" / "a.bin"
	fetcher.fetch_file(m, "a.bin", url, dest)
	first = dict(m.files["a.bin"])
	src.unlink()  # a second fetch must not touch the network
	assert fetcher.fetch_file(m, "a.bin", url, dest) == "cached"
	assert m.files["a.bin"] == first


def test_corrupted_cache_is_refused_without_force(tmp_path):
	src, url = _source(tmp_path)
	m = _manifest(tmp_path)
	dest = tmp_path / "cache" / "a.bin"
	fetcher.fetch_file(m, "a.bin", url, dest)
	dest.write_bytes(b"bit rot!!!!!")
	with pytest.raises(fetcher.ChecksumError):
		fetcher.fetch_file(m, "a.bin", url, dest)
	assert fetcher.fetch_file(m, "a.bin", url, dest, force=True) == "downloaded"
	assert dest.read_bytes() == b"hello ferry"


def test_upstream_change_is_refused_without_force(tmp_path):
	src, url = _source(tmp_path)
	m = _manifest(tmp_path)
	fetcher.fetch_file(m, "a.bin", url, tmp_path / "cache" / "a.bin")
	src.write_bytes(b"changed upstream")
	fresh_dest = tmp_path / "other-machine" / "a.bin"
	with pytest.raises(fetcher.ChecksumError):
		fetcher.fetch_file(m, "a.bin", url, fresh_dest)
	assert not fresh_dest.exists(), "a refused download must not leave a file behind"
	assert not list(fresh_dest.parent.glob("*.part"))
	assert fetcher.fetch_file(m, "a.bin", url, fresh_dest, force=True) == "downloaded"
	assert m.files["a.bin"]["sha256"] == hashlib.sha256(b"changed upstream").hexdigest()


def test_floating_resource_accepts_upstream_change(tmp_path):
	src, url = _source(tmp_path)
	m = _manifest(tmp_path)
	dest = tmp_path / "cache" / "list.json"
	fetcher.fetch_file(m, "list.json", url, dest, pinned=False)
	src.write_bytes(b"newer list")
	dest.unlink()
	assert fetcher.fetch_file(m, "list.json", url, dest, pinned=False) == "downloaded"
	assert m.files["list.json"]["size_bytes"] == len(b"newer list")


def test_verify_cached_reports_missing_and_bad(tmp_path):
	src, url = _source(tmp_path)
	m = _manifest(tmp_path)
	dest = tmp_path / "cache" / "a.bin"
	fetcher.fetch_file(m, "a.bin", url, dest)
	assert m.verify("a.bin", dest) is True
	dest.write_bytes(b"x")
	assert m.verify("a.bin", dest) is False
	assert m.verify("nope", dest) is False


def test_validate_hook_rejects_bad_payload_and_leaves_no_trace(tmp_path):
	src, url = _source(tmp_path, b"Error: no data")
	m = _manifest(tmp_path)
	dest = tmp_path / "cache" / "a.bin"

	def reject(path):
		if path.read_bytes().startswith(b"Error"):
			raise ValueError("api error")

	with pytest.raises(ValueError):
		fetcher.fetch_file(m, "a.bin", url, dest, validate=reject)
	assert not dest.exists() and not list(dest.parent.glob("*.part"))
	assert "a.bin" not in m.files


def test_client_error_is_rejected_without_retry_and_leaves_no_trace(tmp_path):
	import http.server
	import threading

	class Handler(http.server.BaseHTTPRequestHandler):
		hits = 0

		def do_GET(self):
			Handler.hits += 1
			self.send_response(400)
			self.end_headers()
			self.wfile.write(b"Wrong datum for this station")

		def log_message(self, *args):
			pass

	server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
	threading.Thread(target=server.serve_forever, daemon=True).start()
	try:
		m = _manifest(tmp_path)
		dest = tmp_path / "cache" / "a.bin"
		with pytest.raises(fetcher.RequestRejected, match="Wrong datum"):
			fetcher.fetch_file(m, "a.bin", f"http://127.0.0.1:{server.server_port}/x", dest)
		assert Handler.hits == 1
		assert not dest.exists() and "a.bin" not in m.files
	finally:
		server.shutdown()
