"""Failure modes: each berth inherits the default face/normal, overwrites its plot, or claims AIS selected crew berths."""
from types import SimpleNamespace

from shapely.geometry import LineString, Polygon

from ferrydata import landing_audit


def test_single_berth_audit_is_unchanged():
	props = {'stop_id': '8', 'berths': [{'id': 'enc:own'}]}
	assert landing_audit.berth_properties(props, SimpleNamespace(props={}), [], {}) is props


def test_each_berth_uses_its_exported_face_and_source_outline_without_reselection():
	face = LineString([(2, 3), (2, 5)])
	outline = Polygon([(1, 2), (3, 2), (3, 6), (1, 6)])
	berth = {'id': 'enc:N', 'name': 'North pontoon', 'order': 1, 'face_geometry': landing_audit.landings.to_wgs84(face).__geo_interface__, 'outward_normal_ne': [0., 1.]}
	props = {'stop_id': '87', 'stop_name': 'Pier 11', 'berths': [{'id': 'enc:E', 'name': 'Outer pontoon (east)', 'order': 0}, berth],
		'crew_note': {'default_berth_id': 'enc:E'}, 'enc_id': 'E', 'face_geometry': {'type': 'LineString', 'coordinates': [[0, 0], [1, 1]]}}
	item = SimpleNamespace(props={'berth_id': 'enc:N', 'stop_name': 'Pier 11 — North pontoon', 'east_m': 2, 'north_m': 3})
	segment = {'berth_id': 'enc:N', 'berth_face': True, 'water_side_evidence': {'method': 'unverified'}, 'verification': 'crew-reported berth'}
	got = landing_audit.berth_properties(props, item, [(outline, {'id': 'N', 'water_level_effect_code': 7})], {'enc:N': [segment]})
	assert got['face_geometry'] == berth['face_geometry']
	assert got['outward_normal_ne'] == [0., 1.]
	assert got['enc_id'] == 'N' and got['berth_name'] == 'North pontoon'
	assert got['default_berth_name'] == 'Outer pontoon (east)'
	assert got['audit_figure'] == '87-N'
	assert got['water_side_evidence']['method'] == 'unverified'
	assert props['enc_id'] == 'E', 'never mutate the shared stop properties'


def test_pier11_failure_diagnosis_names_crew_use_without_claiming_ramp_side_or_depth():
	text = landing_audit.explanation({'stop_id': '87', 'berth_name': 'North pontoon', 'default_berth_name': 'Outer pontoon (east)'},
		{'min_depth_m': .88, 'shallow_cells': 3, 'sampled_cells': 10}, 2.1)
	assert 'North pontoon' in text and 'Outer pontoon (east)' in text
	assert 'ramp side' in text and 'clearance' in text
	assert 'southern charted pontoon' not in text and 'provisional' not in text
