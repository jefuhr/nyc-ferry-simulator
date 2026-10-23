import pytest

from ferrydata import geoid, paths, slices

LER = slices.LOWER_EAST_RIVER


def test_compute_offsets_covers_centre_and_corners_and_uses_the_transform():
	calls = []

	def fake(lon, lat, h):
		calls.append((lon, lat, h))
		return lon, lat, -32.0 - lat  # distinct per point so a mixed-up point shows

	points = geoid.compute_offsets(LER, fake)
	assert set(points) == {"centre", "sw", "se", "nw", "ne"}
	assert all(c[2] == 0.0 for c in calls)  # NAVD88 zero
	assert points["sw"]["navd88_zero_ellipsoidal_height_m"] == pytest.approx(-32.0 - LER.south - 0.0, abs=1e-4)
	assert points["ne"]["lon"] == LER.east and points["ne"]["lat"] == LER.north
	assert "north_m" in points["centre"] and "east_m" in points["centre"]


def test_lon_lat_are_passed_in_xy_order():
	seen = {}

	def fake(lon, lat, h):
		seen["last"] = (lon, lat)
		return lon, lat, -32.0

	geoid.compute_offsets(LER, fake)
	lon, lat = seen["last"]
	assert -75 < lon < -73 and 40 < lat < 41


@pytest.mark.skipif(
	not (paths.raw_dir("geoid") / geoid.GRID_NAME).exists(),
	reason="GEOID18 grid not in the raw cache; run ferrydata fetch (this test needs the real grid)",
)
def test_real_grid_gives_plausible_offset():
	transformer = geoid.make_transformer()
	points = geoid.compute_offsets(LER, transformer.transform)
	for point in points.values():
		assert -35.0 < point["navd88_zero_ellipsoidal_height_m"] < -29.0
