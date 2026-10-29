"""Rebinding a depth digest must preserve raw day bytes and refuse corrupt inputs before writing."""
from pathlib import Path

import pytest

from ferrydata.environment import harbor


def test_rebind_refuses_corrupt_input_without_mutating_destination(tmp_path):
	source, target = tmp_path / 'source', tmp_path / 'target'
	source.mkdir()
	with pytest.raises((ValueError, FileNotFoundError)):
		harbor.rebuild_days_with_depth(source, tmp_path / 'missing', target)
	assert not target.exists()


def test_rebind_preserves_raw_series_and_changes_all_day_depth_digests(tmp_path):
	import json
	from test_environment_depth_set import write_depth
	from ferrydata.environment import pack, series

	source = tmp_path / 'source'
	old = write_depth(source / 'depth', [('fixture', 0., 0., 5., [[-1., -1.], [-1., -1.]])])
	new = write_depth(tmp_path / 'new', [('fixture', 0., 0., 5., [[-3., -3.], [-3., -3.]])])
	station = dict(id='a', name='fixture', lat=40.7, lon=-74.015, north_m=0., east_m=0., source='fixture.json', source_sha256='0'*64)
	for day in series.DAYS:
		pack.write_day(source, 'harbor', day, pack.read_digest(old),
			[dict(station, kind='harmonic-prediction', bin_depth_m=1.)], [station], [],
			[('a', -3600., 0., 0.), ('a', 90000., 0., 0.)],
			[('a', -3600., 0., 'observed'), ('a', 90000., 0., 'observed')], [], [])
	target = tmp_path / 'adopted'
	assert len(harbor.rebuild_days_with_depth(source, new, target)) == 28
	for day in series.DAYS:
		for name in pack.COLUMNS:
			assert (source / 'days' / day / name).read_bytes() == (target / 'days' / day / name).read_bytes()
		m = json.loads((target / 'days' / day / 'manifest.json').read_text())
		assert m['depth_digest'] == pack.read_digest(new)
		assert pack.read_digest(source / 'days' / day) != pack.read_digest(target / 'days' / day)
	# Explicit in-place adoption is also supported only after verification of the entire old set.
	assert harbor.rebuild_days_with_depth(source, new, source) == pack.verify(target)
