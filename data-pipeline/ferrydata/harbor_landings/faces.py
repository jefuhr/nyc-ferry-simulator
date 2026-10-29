"""Explicit reviewed face confirmations, pinned to the exact existing face geometry.

A changed direction requires rebuilding/reviewing geometry, not setting this flag. A note
confirms face use only; it cannot supply a missing bed or waive depth/contact clearance.
"""
import hashlib
import json
from datetime import date
from pathlib import Path

EXCLUDED_STOPS = frozenset(('113', '118', '137'))


def load_notes(path=None):
	return json.loads(Path(path or Path(__file__).with_name('crew-face-notes.json')).read_text(encoding='utf-8'))['notes']


def geometry_digest(records):
	keys = ('stop_id', 'berth_id', 'start_ne_m', 'end_ne_m', 'water_normal_ne')
	payload = [{k: r[k] for k in keys} for r in records]
	return hashlib.sha256(json.dumps(payload, sort_keys=True, separators=(',', ':'), allow_nan=False).encode('utf-8')).hexdigest()


def face_eligible(stop, berth, records, notes, production_eligible):
	if stop not in EXCLUDED_STOPS:
		return production_eligible
	for note in notes:
		if note.get('stop_id') != stop or note.get('berth_id') != berth:
			continue
		try:
			date.fromisoformat(note['date'])
		except (ValueError, KeyError, TypeError):
			continue
		if (note.get('reviewed') is True and note.get('confirms_existing_face') is True
			and note.get('source') == 'project crew reviewer' and bool(note.get('statement', '').strip())
			and note.get('geometry_sha256') == geometry_digest(records)):
			return True
	return False
