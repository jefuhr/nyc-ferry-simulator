"""Failure modes: reversed normals, invented ENC outlines, landward faces and ambiguous matches."""
import math

import pytest
from shapely.geometry import LineString, Point, Polygon

from ferrydata import landings


def test_synthetic_barge_face_is_outer_long_edge_with_true_heading():
	barge = Polygon([(-14, 20), (14, 20), (14, 30), (-14, 30)])  # east, north
	face, outward = landings.berthing_face(barge, (1, 0))  # north, east
	assert face.equals(LineString([(-14, 30), (14, 30)]))
	assert face.length == pytest.approx(28)
	assert outward == pytest.approx((1, 0))
	assert landings.heading_deg(outward) == pytest.approx(0)
	assert landings.heading_deg((0, -1)) == pytest.approx(270)


def test_rotated_barge_normal_remains_perpendicular_to_face():
	from shapely.affinity import rotate
	barge = rotate(Polygon([(-14, 20), (14, 20), (14, 30), (-14, 30)]), -35, origin=(0, 0))
	face, outward = landings.berthing_face(barge, (math.cos(math.radians(35)), math.sin(math.radians(35))))
	assert landings.heading_deg(outward) == pytest.approx(35)
	a, b = face.coords
	assert (b[0] - a[0]) * outward[1] + (b[1] - a[1]) * outward[0] == pytest.approx(0, abs=1e-8)


def test_derived_barge_dimensions_and_gangway_are_explicit():
	barge = landings.derive_barge((0, 0), (1, 0), (1, 0), Point(0, 0))
	assert barge.area == pytest.approx(35 * 90 * .3048 ** 2)
	assert barge.bounds[1] == pytest.approx(85 * .3048)
	assert barge.bounds[3] == pytest.approx(120 * .3048)


def test_water_side_uses_charted_water_and_rejects_land_and_inward_normal():
	water = Polygon([(-100, 0), (100, 0), (100, 100), (-100, 100)])
	land = Polygon([(-100, -100), (100, -100), (100, 0), (-100, 0)])
	face = LineString([(-14, 30), (14, 30)])
	assert landings.face_on_water_side(face, (1, 0), (0, 0), water, land)
	assert not landings.face_on_water_side(face, (-1, 0), (0, 0), water, land)
	assert not landings.face_on_water_side(LineString([(-14, -30), (14, -30)]), (1, 0), (0, 0), water, land)


def test_ambiguous_enc_polygon_match_is_marked_for_review():
	stop = Point(0, 0)
	candidates = [(Polygon([(-15, 20), (-5, 20), (-5, 45), (-15, 45)]), {'id': 'a'}),
		(Polygon([(5, 20), (15, 20), (15, 45), (5, 45)]), {'id': 'b'})]
	_, _, ambiguous = landings.match_barge(stop, candidates)
	assert ambiguous


def test_face_uses_the_water_immediately_outside_an_enc_barge_hole():
	from shapely.geometry import box
	barge = box(-14, 20, 14, 30)
	water = box(-100, 0, 100, 100).difference(barge.buffer(.01))
	land = box(-100, -100, 100, 0)
	assert landings.face_on_water_side(LineString([(-14, 30), (14, 30)]), (1, 0), (0, 0), water, land)


def test_subdivided_enc_long_face_is_kept_whole_instead_of_short_end():
	barge = Polygon([(-5, 20), (5, 20), (5, 30), (5, 40), (5, 50), (-5, 50), (-5, 40), (-5, 30)])
	face, normal = landings.berthing_face(barge, (.9, .1))
	assert face.length == pytest.approx(30)
	assert normal == pytest.approx((0, 1))
