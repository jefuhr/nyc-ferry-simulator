"""Failures: hidden coverage reduction, wrong height units, artificial tile-edge shorelines and part-based selection."""
import pytest
from shapely.geometry import LineString,box
from ferrydata.buildings.selection import Shoreline,keep,policy_record


def test_waterfront_and_tall_buildings_keep_explicit_source_units():
	shore=Shoreline([LineString([(0,-10000),(0,10000)])])
	assert keep(box(599,0,610,10),9,shore,'B')
	assert not keep(box(601,0,610,10),9,shore,'B')
	assert keep(box(2000,0,2010,10),25,shore,'B')
	assert not keep(box(2000,0,2010,10),24.99,shore,'B')
	assert keep(box(399,0,410,10),9,shore,'C')
	assert keep(box(2000,0,2010,10),40,shore,'C')
	assert not keep(box(401,0,410,10),39.99,shore,'C')


def test_original_building_is_selected_before_tile_fragmentation():
	shore=Shoreline([LineString([(0,-10000),(0,10000)])])
	p=box(590,0,1000,10)
	assert keep(p,9,shore,'B')
	assert not keep(box(800,0,1000,10),9,shore,'B')
	assert policy_record('B')['selection_stage']=='original footprint before tile/land clipping'


def test_all_policy_has_no_silent_filter_or_missing_shoreline_dependency():
	assert keep(box(99999,99999,100000,100000),9,None,'A')
	with pytest.raises(ValueError,match='shoreline'):
		Shoreline([])
