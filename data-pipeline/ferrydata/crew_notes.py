"""Dated, tracked crew statements. They supplement charts; they never edit chart data."""
import hashlib
import json

from ferrydata import paths


def load():
	file = paths.MANIFEST_DIR / 'crew-landing-notes.json'
	if not file.exists():
		return {}, None
	raw = file.read_bytes()
	payload = json.loads(raw)
	notes = {}
	for note in payload['notes']:
		if not all(note.get(key) for key in ('stop_id', 'statement', 'date', 'source')):
			raise ValueError('Crew landing note lacks identity, statement or provenance')
		if note['stop_id'] in notes:
			raise ValueError('Duplicate crew landing note: ' + note['stop_id'])
		notes[note['stop_id']] = note
	return notes, hashlib.sha256(raw).hexdigest()
