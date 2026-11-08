"""Guard provenance, waterline datum, layout-driven shape and mount/identity drift."""
import copy
import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import dimensions as subject

ROOT = Path(__file__).resolve().parents[3]
MATERIALS = ['HullWhite', 'DeckGrey', 'WindowGlass', 'DarkMetal', 'BlackFender', 'RailMetal', 'CabinWhite']


class DimensionsTests(unittest.TestCase):
	def vessel(self, name):
		return json.loads((ROOT / 'vessels' / (name + '.json')).read_text())

	def test_layout_drives_shape_independently_of_physics_windage(self):
		for name in ('t-class', 'k-class'):
			v = self.vessel(name)
			layout = subject.load_layout(name)
			d = subject.dimensions(v, layout)
			for key in ('length', 'beam', 'draft', 'freeboard', 'upper_deck_height', 'wheelhouse_roof_height'):
				self.assertEqual(d[key], layout['values'][subject.ALIASES.get(key, key)]['value'] * (-1 if key == 'draft' else 1))
			v['windage']['frontalArea']['value'] *= 1.5
			v['windage']['lateralArea']['value'] *= 1.5
			changed = subject.dimensions(v, layout)
			for key in d:
				if key not in ('eye_height', 'eye_forward', 'eye_position'):
					self.assertEqual(d[key], changed[key], key)

	def test_missing_or_invalid_measured_values_fail_instead_of_formula_fallback(self):
		layout = subject.load_layout('t-class')
		for mutate in (
			lambda x: x['values'].pop('upper_deck_height'),
			lambda x: x['values']['main_deck_height'].update(value=float('nan')),
			lambda x: x['values']['main_deck_height'].pop('source'),
			lambda x: x['values']['main_deck_height'].update(confidence='certain'),
			lambda x: x['values']['length_overall'].update(unit='ratio'),
		):
			bad = copy.deepcopy(layout)
			mutate(bad)
			with self.assertRaises((ValueError, KeyError)):
				subject.dimensions(self.vessel('t-class'), bad)

	def test_new_layout_value_changes_dimensions_and_bow_mount(self):
		layout = subject.load_layout('t-class')
		layout['values']['main_deck_height']['value'] += .3
		layout['values']['bow_height']['value'] += .3
		layout['values']['wheelhouse_fore_z']['value'] -= .5
		d = subject.dimensions(self.vessel('t-class'), layout)
		self.assertEqual(d['freeboard'], layout['values']['main_deck_height']['value'])
		self.assertEqual(d['wheelhouse_forward'], layout['values']['wheelhouse_fore_z']['value'])
		self.assertEqual(subject.mounts(d)['Bow'][1], layout['values']['bow_height']['value'])

	def test_eye_uses_interior_value_if_present_and_legacy_formula_otherwise(self):
		v = self.vessel('t-class')
		layout = subject.load_layout('t-class')
		d = subject.dimensions(v, layout, wheelhouse={"values": {"eye_position": {
			"value": [.1, 6.4, 5.2], "unit": "m", "source": "crew-confirmed",
			"method": "crew-confirmed", "confidence": "high"}}})
		self.assertEqual(subject.mounts(d)['Eye'], [.1, 6.4, 5.2])
		d = subject.dimensions(v, layout, wheelhouse={"values": {}})
		self.assertAlmostEqual(d['eye_height'], 6.949343832020998)
		self.assertAlmostEqual(d['eye_forward'], 7.874903934126985)

	def test_interior_seated_eye_uses_its_floor_relative_contract(self):
		def point(value):
			return dict(value=value, unit='m', source='crew-confirmed', method='crew-confirmed', confidence='high')
		v = self.vessel('t-class')
		layout = subject.load_layout('t-class')
		floor = layout['values']['upper_deck_height']['value']
		interior = {'values': {'floor_origin': point([0,floor,4.5]), 'eye_seated': point([-.28,1.8,5.75])}}
		d = subject.dimensions(v, layout, wheelhouse=interior)
		self.assertEqual(subject.mounts(d)['Eye'], [-.28,floor+1.8,5.75])
		interior['values']['floor_origin']['value'][1] += .2
		with self.assertRaises(ValueError):
			subject.dimensions(v, layout, wheelhouse=interior)

	def test_every_layout_number_has_provenance_and_palette_is_pinned(self):
		for name in ('t-class', 'k-class'):
			layout = subject.load_layout(name)
			self.assertEqual(layout['subject'], name)
			self.assertEqual(layout['kind'], 'hull')
			self.assertEqual([m['name'] for m in layout['materials']], MATERIALS)
			self.assertEqual([m['colour'] for m in layout['materials']], [
				[.82,.85,.85], [.37,.41,.43], [.025,.055,.065], [.19,.23,.25],
				[.055,.065,.07], [.65,.69,.70], [.93,.93,.91]])
			for value in layout['values'].values():
				for item in ([value] if 'value' in value else value.values()):
					self.assertTrue(item['source'])
					self.assertIn(item['confidence'], ('high', 'medium', 'low'))
					self.assertIn(item['method'], ('drawing-scale', 'known-dimension', 'photo-estimate', 'video-estimate', 'crew-confirmed', 'published', 'simulator-estimate'))

	def test_mounts_are_layout_values_and_satisfy_navigation_placement(self):
		for name in ('t-class', 'k-class'):
			layout = subject.load_layout(name)
			d = subject.dimensions(self.vessel(name), layout)
			m = subject.mounts(d)
			for key, value in layout['values']['light_mounts'].items():
				self.assertEqual(m[key], value['value'])
			self.assertEqual(m['Bow'], [0, d['bow_height'], .5 * d['length']])
			self.assertEqual(m['PortSidelight'][0], -m['StarboardSidelight'][0])
			self.assertLess(m['PortSidelight'][0], 0)
			self.assertGreaterEqual(m['Masthead'][1] - d['freeboard'], 6)
			self.assertGreaterEqual(m['Masthead'][1], m['StarboardSidelight'][1] + 2.5)
			self.assertGreater(m['Masthead'][2], m['StarboardSidelight'][2])
			self.assertLess(m['Sternlight'][2], 0)

	def test_hull_stations_preserve_printed_waterline_length(self):
		for name in ('t-class', 'k-class'):
			v = subject.load_layout(name)['values']
			points = [p['value'] for p in v['hull_stations'].values()]
			bow_waterline = next((p[0] for p in points if p[2] == 0), None)
			self.assertIsNotNone(bow_waterline, 'A station explicitly anchors the waterline entrance')
			self.assertAlmostEqual(bow_waterline - points[0][0], v['length_waterline']['value'])

	def test_identity_is_from_audit_not_similar_dimensions_or_name(self):
		classes = subject.audited_classes(ROOT)
		self.assertEqual(classes[367798420], 't-class')
		self.assertEqual(classes[368135270], 'k-class')
		self.assertNotIn(368152730, classes)
		self.assertNotIn(366651000, classes)


if __name__ == '__main__':
	unittest.main()
