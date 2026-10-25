"""Traffic failure modes: spatial rejection, identity/time duplicates, gaps and jumps,
AIS sentinels/types, fixed obstacles, audited ferries, binary compatibility and determinism.
"""

import json
import struct

import numpy as np
import pandas as pd
import pytest

from ferrydata.traffic import (
	RECORD_DTYPE, build_pack, clean_reports, epoch_utc, ferry_candidates,
	ship_class, verify_pack,
)


def points(**columns):
	n = max((len(v) for v in columns.values()), default=1)
	base = dict(mmsi=[367798420] * n, base_date_time=[f"2025-07-14T12:00:{i:02d}Z" for i in range(n)],
		latitude=[40.7] * n, longitude=[-74.0] * n, sog=[0.] * n, cog=[360.] * n,
		heading=[511.] * n, vessel_name=["TEST"] * n, vessel_type=[60.] * n,
		length=[26.] * n, width=[8.] * n, draft=[1.5] * n)
	base.update(columns)
	return pd.DataFrame(base)


@pytest.mark.parametrize("day,expected", [
	("2024-10-07", "2024-10-07T04:00:00Z"), ("2025-01-13", "2025-01-13T05:00:00Z"),
	("2025-04-14", "2025-04-14T04:00:00Z"), ("2025-07-14", "2025-07-14T04:00:00Z"),
])
def test_epoch(day, expected):
	assert epoch_utc(day) == expected


def test_bbox_edges_impossible_and_outside():
	frame = points(latitude=[40.5, 40.9, 40.49, 91., np.nan], longitude=[-74.1, -73.75, -74., -74., -74.], mmsi=[1, 2, 3, 4, 5])
	kept, drops = clean_reports(frame, "2025-07-14")
	assert list(kept.mmsi) == [1, 2]
	assert drops["impossible_position"] == 2
	assert drops["outside_bbox"] == 1


def test_duplicates_sort_and_segments_per_vessel():
	frame = points(mmsi=[2, 1, 1, 1, 1, 2], base_date_time=[
		"2025-07-14T12:00:00Z", "2025-07-14T12:10:01Z", "2025-07-14T12:00:00Z",
		"2025-07-14T12:00:00Z", "2025-07-14T12:05:00Z", "2025-07-14T12:05:01Z"])
	kept, drops = clean_reports(frame, "2025-07-14")
	assert list(zip(kept.mmsi, kept.segment)) == [(1, 0), (1, 0), (1, 1), (2, 0), (2, 1)]
	assert drops["duplicate"] == 1


def test_jump_uses_previous_kept_and_low_speed_slack():
	# At rest the 10 kn allowance plus 30 m admits 35 m in 1 s, not 40 m.
	from ferrydata.geodesy import from_local
	lat, lon = from_local(np.array([0., 35., 1000., 36.]), np.zeros(4))
	kept, drops = clean_reports(points(latitude=lat, longitude=lon), "2025-07-14")
	assert len(kept) == 3
	assert drops["position_jump"] == 1
	assert kept.north_m.iloc[-1] == pytest.approx(36., abs=1e-5)


def test_jump_uses_max_endpoint_sog():
	from ferrydata.geodesy import from_local
	lat, lon = from_local(np.array([0., 60.]), np.zeros(2))
	kept, _ = clean_reports(points(latitude=lat, longitude=lon, sog=[60., 0.]), "2025-07-14")
	assert len(kept) == 2


def test_nan_sentinels_and_valid_angles():
	kept, _ = clean_reports(points(cog=[360., 90., -1.], heading=[511., 359., -1.], sog=[102.3, 5., -1.]), "2025-07-14")
	assert np.isnan(kept.cog_rad.iloc[0]) and np.isnan(kept.heading_rad.iloc[0])
	assert np.isnan(kept.cog_rad.iloc[2]) and np.isnan(kept.heading_rad.iloc[2])
	assert np.isnan(kept.sog_mps.iloc[0]) and np.isnan(kept.sog_mps.iloc[2])
	assert kept.cog_rad.iloc[1] == pytest.approx(np.pi / 2)


@pytest.mark.parametrize("codes,expected", [
	(range(60, 70), "ferry-passenger"), (range(70, 80), "cargo"), (range(80, 90), "tanker"),
	([31, 32, 52], "tug-tow"), ([36], "sailing"), ([37], "pleasure"), ([30], "fishing"),
	([55], "law-enforcement"), ([50], "pilot"), ([51], "search-rescue"), ([35], "military"),
	([0, 20, 29, 33, 34, 38, 40, 49, 53, 54, 56, 59, 90, 99, 100, -1, None, np.nan, 60.5], "other"),
])
def test_classes(codes, expected):
	assert all(ship_class(code) == expected for code in codes)


def test_identity_audit_only_t_and_k(tmp_path):
	path = tmp_path / "audit.csv"
	path.write_text("mmsi,class\n1,T\n2,K\n3,unclassified\n")
	assert ferry_candidates(path) == {1, 2}
	assert 367798420 in ferry_candidates()


def test_binary_metadata_fixed_obstacle_and_roundtrip(tmp_path):
	out = tmp_path / "pack"
	manifest = build_pack(points(), "2025-07-14", out)
	vessels = pd.read_csv(out / "vessels.csv")
	assert not bool(vessels.moving.iloc[0])
	assert bool(vessels.ferry_candidate.iloc[0])
	layout = json.loads((out / "reports.json").read_text())
	assert layout["record_size_bytes"] == RECORD_DTYPE.itemsize == 44
	assert [(f["name"], f["offset_bytes"]) for f in layout["fields"]] == [
		("vessel_index", 0), ("segment", 4), ("t_s", 8), ("north_m", 16),
		("east_m", 24), ("sog_mps", 32), ("cog_rad", 36), ("heading_rad", 40)]
	row = struct.unpack("<i idddfff".replace(" ", ""), (out / "reports.bin").read_bytes())
	assert row[:3] == (0, 0, 28800.)
	assert np.isnan(row[-1])
	assert manifest["counts"]["reports"] == 1
	verify_pack(out)


def test_moving_and_deterministic_bytes(tmp_path):
	frame = points(latitude=[40.7, 40.701], sog=[10., 10.], base_date_time=["2025-07-14T12:00:00Z", "2025-07-14T12:01:00Z"])
	for folder in ["one", "two"]:
		build_pack(frame, "2025-07-14", tmp_path / folder)
	assert pd.read_csv(tmp_path / "one/vessels.csv").moving.iloc[0]
	for name in ["manifest.json", "vessels.csv", "reports.bin", "reports.json"]:
		assert (tmp_path / "one" / name).read_bytes() == (tmp_path / "two" / name).read_bytes()


def test_empty_and_corruption_rejected(tmp_path):
	build_pack(points(latitude=[0.]), "2025-07-14", tmp_path)
	verify_pack(tmp_path)
	(tmp_path / "reports.bin").write_bytes(b"bad")
	with pytest.raises(ValueError):
		verify_pack(tmp_path)


def test_local_day_adjacent_source_coverage_and_missing_tail(tmp_path):
	from ferrydata.traffic import scenario_window
	window = scenario_window("2025-07-14", {"2025-07-14", "2025-07-15"})
	assert window == {"requested_from_s": -3600., "requested_to_s": 90000., "covered_from_s": -3600., "covered_to_s": 90000., "missing_intervals": []}
	window = scenario_window("2025-01-19", {"2025-01-19"})
	assert window["covered_to_s"] == 68400.
	assert window["missing_intervals"] == [{"from_s": 68400., "to_s": 90000., "reason": "UTC source ais-2025-01-20.csv.zst is not cached"}]


def test_streaming_keeps_all_sizes_types_and_accounts_drops(tmp_path):
	import pyarrow as pa
	from ferrydata.traffic import read_day
	frame = points(mmsi=[1, 2, 3], latitude=[40.7, 40.7, 0.], length=[200., 5., 26.], vessel_type=[80., 37., 60.])
	path = tmp_path / "ais.csv.zst"
	with pa.CompressedOutputStream(str(path), "zstd") as stream:
		stream.write(frame.to_csv(index=False).encode())
	kept, drops, total = read_day(path)
	assert list(kept.mmsi) == [1, 2]
	assert total == 3 and drops["outside_bbox"] == 1


def test_jump_missing_sog_uses_only_allowance_and_slack():
	from ferrydata.geodesy import from_local
	lat, lon = from_local(np.array([0., 35., 100.]), np.zeros(3))
	kept, drops = clean_reports(points(latitude=lat, longitude=lon, sog=[np.nan] * 3), "2025-07-14")
	assert len(kept) == 2 and drops["position_jump"] == 1


def test_invalid_identity_and_time_are_accounted():
	kept, drops = clean_reports(points(mmsi=[0, 1, 2], base_date_time=["bad", "bad", "2025-07-14T00:00:00Z"]), "2025-07-14")
	assert list(kept.mmsi) == [2]
	assert kept.t_s.iloc[0] == -14400.
	assert drops["invalid_mmsi"] == drops["invalid_time"] == 1


def test_spatial_extract_cache_reuses_and_rejects_corruption(tmp_path, monkeypatch):
	from ferrydata import traffic
	calls = []
	def read(_):
		calls.append(True)
		return points(), {reason: 0 for reason in traffic.DROP_REASONS}, 1
	monkeypatch.setattr(traffic, "read_day", read)
	one = traffic.cached_read_day(tmp_path / "source", "fixture-sha", tmp_path)
	two = traffic.cached_read_day(tmp_path / "source", "fixture-sha", tmp_path)
	assert len(calls) == 1
	pd.testing.assert_frame_equal(one[0], two[0])
	assert one[1:] == two[1:]
	(tmp_path / "fixture-sha.parquet").write_bytes(b"corrupt")
	traffic.cached_read_day(tmp_path / "source", "fixture-sha", tmp_path)
	assert len(calls) == 2


def test_digest_matches_independent_spec_and_sorted_binary(tmp_path):
	import hashlib
	frame = points(mmsi=[2, 1], base_date_time=["2025-07-14T12:00:01Z", "2025-07-14T12:00:00Z"])
	manifest = build_pack(frame, "2025-07-14", tmp_path)
	payload = b"".join(name.encode() + b"\0" + (tmp_path / name).read_bytes() + b"\0"
		for name in ("vessels.csv", "reports.bin", "reports.json"))
	assert manifest["digest"] == hashlib.sha256(payload + b"2025-07-14T04:00:00Z\0").hexdigest()
	records = np.fromfile(tmp_path / "reports.bin", dtype=RECORD_DTYPE)
	assert list(records["vessel_index"]) == [0, 1]
	assert list(records["t_s"]) == [28800., 28801.]


def test_parked_barge_uses_median_radius_not_total_track_extent(tmp_path):
	from ferrydata.geodesy import from_local
	lat, lon = from_local(np.array([-40., 0., 40.]), np.zeros(3))
	frame = points(mmsi=[1] * 3, latitude=lat, longitude=lon, vessel_type=[90.] * 3, vessel_name=["PARKED BARGE"] * 3,
		base_date_time=["2025-07-14T12:00:00Z", "2025-07-14T12:01:00Z", "2025-07-14T12:02:00Z"])
	build_pack(frame, "2025-07-14", tmp_path)
	vessel = pd.read_csv(tmp_path / "vessels.csv").iloc[0]
	assert vessel["reports"] == 3
	assert vessel["moving"] == False
	assert vessel["ferry_candidate"] == False


def test_scenario_input_combines_utc_days_and_filters_padded_window(tmp_path, monkeypatch):
	from types import SimpleNamespace
	from ferrydata import traffic
	files = {}
	for day in ("2025-07-14", "2025-07-15"):
		name = f"ais-{day}.csv.zst"
		(tmp_path / name).touch()
		files[name] = {"name": name, "url": "fixture", "sha256": "fixture", "size_bytes": 0}
	monkeypatch.setattr(traffic.paths, "raw_dir", lambda _: tmp_path)
	monkeypatch.setattr(traffic.Manifest, "load", lambda _: SimpleNamespace(files=files, verify=lambda *_: True))
	def read(path):
		times = (["2025-07-14T02:59:59Z", "2025-07-14T03:00:00Z", "2025-07-14T23:59:59Z"]
			if "2025-07-14" in path.name else ["2025-07-15T00:00:00Z", "2025-07-15T05:00:00Z", "2025-07-15T05:00:01Z"])
		return points(base_date_time=times), {reason: 0 for reason in traffic.DROP_REASONS}, 3
	monkeypatch.setattr(traffic, "read_day", read)
	frame, drops, count, sources, coverage = traffic.scenario_input("2025-07-14")
	assert len(frame) == 4 and count == 6 and drops["outside_time_window"] == 2
	assert len(sources) == 2 and coverage["missing_intervals"] == []
	kept, _ = clean_reports(frame, "2025-07-14")
	assert kept.t_s.min() == -3600. and kept.t_s.max() == 90000.
