import numpy as np
import pytest

from ferrydata import geodesy

# Reference vectors copied from core/dotnet/FerrySim.Core.Tests/Contracts/LocalTangentPlaneTests.cs
REFERENCE = [
	(40.7100, -74.0150, 1110.4821, 0.0000, 0.0969),
	(40.7000, -74.0050, 0.0481, 845.1551, 0.0559),
	(40.5800, -73.8200, -13307.3042, 16510.0303, 35.2544),
	(40.8100, -73.8600, 12226.9375, 13078.3146, 25.1375),
]


@pytest.mark.parametrize("lat,lon,north,east,down", REFERENCE)
def test_to_ned_matches_csharp_reference(lat, lon, north, east, down):
	n, e, d = geodesy.to_ned(lat, lon)
	assert n == pytest.approx(north, abs=1e-3)
	assert e == pytest.approx(east, abs=1e-3)
	assert d == pytest.approx(down, abs=1e-3)


def test_origin_maps_to_zero():
	n, e, d = geodesy.to_ned(geodesy.ORIGIN_LAT, geodesy.ORIGIN_LON)
	assert abs(n) < 1e-6 and abs(e) < 1e-6 and abs(d) < 1e-6


def test_vectorised_matches_scalar():
	lats = np.array([r[0] for r in REFERENCE])
	lons = np.array([r[1] for r in REFERENCE])
	n, e, d = geodesy.to_ned(lats, lons)
	assert n.shape == (4,)
	for i, ref in enumerate(REFERENCE):
		assert n[i] == pytest.approx(ref[2], abs=1e-3)
		assert e[i] == pytest.approx(ref[3], abs=1e-3)


def test_north_east_is_not_flat_scaling():
	# A flat lat/lon scaling is off by tens of metres across the harbor; guard against regressing to it.
	n, e = geodesy.to_local(40.5800, -73.8200)
	flat_north = (40.5800 - 40.7000) * 111_320.0
	assert abs(n - flat_north) > 10.0


def test_round_trip():
	for lat, lon in [(40.58, -73.82), (40.81, -73.86), (40.643, -74.074), (40.72, -73.99)]:
		n, e = geodesy.to_local(lat, lon)
		lat2, lon2 = geodesy.from_local(n, e)
		assert lat2 == pytest.approx(lat, abs=1e-9)
		assert lon2 == pytest.approx(lon, abs=1e-9)


def test_inputs_are_lat_lon_order():
	# Swapping arguments must visibly break the answer (lon/lat mixups are the classic bug).
	n, _ = geodesy.to_local(40.71, -74.015)
	assert n == pytest.approx(1110.4821, abs=1e-3)
