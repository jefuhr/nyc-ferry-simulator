"""Failures: unit/datum errors, invalid heights, courtyard fill, seams, duplicate IDs and water intrusion."""
import numpy as np
import pytest
from shapely.geometry import Polygon, box
from ferrydata.buildings.geometry import dimensions, clip_parts, extrude, deduplicate
from ferrydata.world3d import frames, glb, heights


def test_nyc_roof_is_above_ground_and_feet_convert():
	d = dimensions("nyc", {"heightroof": "100", "groundelev": "20"}, terrain_ground=99)
	assert d["height_m"] == pytest.approx(30.48)
	assert d["ground_navd88_m"] == pytest.approx(6.096)
	assert d["roof_navd88_m"] == pytest.approx(36.576)


@pytest.mark.parametrize("tags,height,basis", [({"height": "100 ft"},30.48,"height"),
	({"height": "18 m"},18,"height"), ({"building:levels":"4"},12,"levels-estimate"),
	({"height":"nan","building:levels":"2"},6,"levels-estimate"), ({},9,"default-estimate")])
def test_osm_height_fallbacks(tags, height, basis):
	d = dimensions("osm-nj", tags, terrain_ground=4)
	assert d["height_m"] == pytest.approx(height)
	assert d["height_basis"] == basis
	assert d["ground_navd88_m"] == 4


def test_missing_ground_cannot_float_at_zero():
	with pytest.raises(ValueError, match="ground"):
		dimensions("osm-nj", {}, terrain_ground=np.nan)


def test_seam_is_split_without_overlapping_area_and_unique_part_ids():
	p = box(3990, 100, 4010, 120)  # x east, y north
	parts = clip_parts(p, [(0,0),(0,1)], box(-100,-100,9000,9000))
	assert len(parts) == 2
	assert sum(g.area for _,g in parts) == pytest.approx(p.area)
	assert parts[0][1].intersection(parts[1][1]).area == 0
	assert {ij for ij,_ in parts} == {(0,0),(0,1)}


def test_seam_touch_does_not_make_second_building_and_water_is_removed():
	p = box(3990,100,4000,120)
	assert len(clip_parts(p, [(0,0),(0,1)], box(-1,-1,9000,9000))) == 1
	parts = clip_parts(box(10,10,30,30), [(0,0)], box(0,0,20,40))
	assert sum(g.area for _,g in parts) == 200


def test_duplicate_source_records_conflict_loudly():
	f = {"id":"nyc:1", "geometry":{"type":"Polygon","coordinates":[]}, "properties":{}}
	assert len(deduplicate([f,f])) == 1
	with pytest.raises(ValueError, match="conflicting"):
		deduplicate([f,{**f,"properties":{"height":3}}])


def test_extrusion_has_outward_winding_roof_hole_and_roundtrip():
	p = Polygon([(100,100),(120,100),(120,120),(100,120)], [[(105,105),(105,115),(115,115),(115,105)]])
	frame = frames.TileFrame(0,0)
	geoid = lambda n,e: np.zeros_like(n)-32
	arrays = extrude(p, 4, 24, frame, geoid)
	document, decoded = glb.decode(glb.encode(*arrays))
	pos = frames.enu_from_gltf(decoded["POSITION"])
	idx = decoded["indices"].reshape(-1,3)
	tri = pos[idx]
	cross = np.cross(tri[:,1]-tri[:,0],tri[:,2]-tri[:,0])
	assert np.all(np.linalg.norm(cross,axis=1)>0)
	assert np.all(np.einsum("ij,ij->i",cross,frames.enu_from_gltf(decoded["NORMAL"])[idx[:,0]]) > 0)
	# Horizontal roof area excludes the 100 m² courtyard (curvature changes projection by < .1%).
	roof = frames.enu_from_gltf(decoded["NORMAL"])[idx[:,0],2] > .99
	assert np.sum(cross[roof,2])/2 == pytest.approx(300,rel=.001)
	assert glb.encode(*arrays) == glb.encode(*arrays)


def test_concave_outline_is_not_convexified():
	p=Polygon([(100,100),(120,100),(120,110),(110,110),(110,120),(100,120)])
	arrays=extrude(p,0,15,frames.TileFrame(0,0),lambda n,e:np.zeros_like(n)-32)
	_,decoded=glb.decode(glb.encode(*arrays))
	pos=frames.enu_from_gltf(decoded['POSITION'])
	idx=decoded['indices'].reshape(-1,3)
	normals=frames.enu_from_gltf(decoded['NORMAL'])[idx[:,0]]
	tri=pos[idx]
	cross=np.cross(tri[:,1]-tri[:,0],tri[:,2]-tri[:,0])
	assert np.sum(cross[normals[:,2]>.99,2])/2==pytest.approx(300,rel=.001)


def test_courtyard_touching_outer_ring_does_not_leave_unused_zero_normals():
	p=Polygon([(100,100),(120,100),(120,120),(100,120),(100,110)],
		[[(100,110),(105,105),(110,110),(105,115)]])
	assert p.is_valid
	arrays=extrude(p,4,24,frames.TileFrame(0,0),lambda n,e:np.zeros_like(n)-32)
	_,decoded=glb.decode(glb.encode(*arrays))
	assert np.isfinite(decoded['NORMAL']).all()
	assert len(np.unique(decoded['indices']))==len(decoded['POSITION'])
