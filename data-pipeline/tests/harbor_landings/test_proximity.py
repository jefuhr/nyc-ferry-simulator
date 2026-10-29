"""Footprint/corridor classification is geometric evidence, not a driven departure or route proof."""
from shapely.geometry import Polygon, Point
from ferrydata.harbor_landings import proximity


def test_first_100m_is_the_exact_straight_hull_extrusion():
	outline = [(0., 0.), (2., 0.), (2., 1.), (0., 1.)]
	region = proximity.extrude(outline, (0., 100.))
	assert region.area == 202.
	assert region.covers(Point(1., 0.))
	assert region.covers(Point(1., 100.5))
	assert not region.covers(Point(3., 50.))
	assert not region.covers(Point(1., 101.01))
