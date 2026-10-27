"""Guard frame reflection, changed source dimensions, mount drift and identity guesses."""
import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from dimensions import dimensions, mounts, audited_classes

ROOT = Path(__file__).resolve().parents[3]


class DimensionsTests(unittest.TestCase):
	def test_matches_greybox_formula_and_source(self):
		for name in ('t-class', 'k-class'):
			v = json.loads((ROOT / 'vessels' / (name + '.json')).read_text())
			d = dimensions(v)
			self.assertEqual(d['length'], v['particulars']['lengthOverall']['value'])
			self.assertEqual(d['beam'], v['particulars']['beam']['value'])
			self.assertAlmostEqual(d['freeboard'], v['particulars']['depth']['value'] - v['particulars']['draft']['value'])
			self.assertAlmostEqual(d['cabin_height'], min(5, max(2, v['windage']['frontalArea']['value'] / d['beam'] - d['freeboard'])))
			self.assertAlmostEqual(d['eye_height'], d['freeboard'] + d['cabin_height'] + 1.7)
			self.assertAlmostEqual(d['eye_forward'], .5 * d['cabin_length'] - 2)

	def test_mounts_are_in_unity_starboard_up_forward_frame(self):
		for name in ('t-class', 'k-class'):
			d = dimensions(json.loads((ROOT / 'vessels' / (name + '.json')).read_text()))
			m = mounts(d)
			self.assertEqual(m['Eye'], [0, d['eye_height'], d['eye_forward']])
			self.assertEqual(m['Bow'], [0, d['freeboard'], .5 * d['length']])
			self.assertEqual(m['PortSidelight'][0], -m['StarboardSidelight'][0])
			self.assertLess(m['PortSidelight'][0], 0)
			self.assertGreater(m['Masthead'][1], m['StarboardSidelight'][1])
			self.assertGreater(m['Masthead'][2], m['StarboardSidelight'][2])
			self.assertLess(m['Sternlight'][2], 0)

	def test_identity_is_from_audit_not_similar_dimensions_or_name(self):
		classes = audited_classes(ROOT)
		self.assertEqual(classes[367798420], 't-class')
		self.assertEqual(classes[368135270], 'k-class')
		self.assertNotIn(368152730, classes)  # Dimension conflict in audit.
		self.assertNotIn(366651000, classes)  # Unclassified passenger boat.


if __name__ == '__main__':
	unittest.main()
