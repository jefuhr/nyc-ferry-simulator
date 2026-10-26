"""validate --harbor's per-tile output list. Failure mode guarded: a slice-only output (the landing faces' depth diff, or
the operational-ais depth floor that only the slice applies) required of every harbor tile, so every tile fails 'outputs'
and its later checks never run."""

from ferrydata import operational_depth, validate


def test_harbor_tiles_do_not_require_the_slice_only_outputs():
	required = validate.harbor_tile_required_outputs()
	for name in ("landing_faces.geojson", "landing_depth_diff.csv", operational_depth.FLOOR_FILE, operational_depth.LOWERED_FILE,
			operational_depth.REPORT_FILE):
		assert name not in required, name


def test_harbor_tiles_still_require_their_own_outputs():
	required = validate.harbor_tile_required_outputs()
	for name in ("raw_sources.json", "depth_chart_navd88.tif", "depth_merged_navd88.tif", "landings.geojson", "coops_stations.json"):
		assert name in required, name
