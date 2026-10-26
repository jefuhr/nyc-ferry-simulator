"""Transit routes and checks for the harbor, offline. Failure modes guarded: a planned route whose legs leave the water
(below the planning depth), a route that is not deterministic, an unreachable end not reported; seam crossings missed or
misplaced along a track; and a transit passed with a depth miss, a time hold or no arrival.
"""
import json

import numpy as np
import pytest

from ferrydata.environment import harbor_transit, pack
from test_environment_depth_set import write_depth


@pytest.fixture
def channel(tmp_path):
	"""A 2 km square of 10 m cells: land (bed +2 m) everywhere but an L-shaped channel 10 m deep, running north 300 m
	wide up the middle, then east along the top."""
	n = np.arange(201)[:, None] * 10.
	e = np.arange(201)[None, :] * 10.
	water = ((np.abs(e - 1000.) <= 150.) & (n <= 1650.)) | ((n >= 1350.) & (n <= 1650.) & (e >= 850.))
	values = np.where(water, -10., 2.) + 0. * n + 0. * e
	return pack.load_depth(write_depth(tmp_path / 'depth', [('only', 0., 0., 10., values.tolist())]))


def test_a_planned_route_keeps_to_the_water_and_is_deterministic(channel):
	route = harbor_transit.plan(channel, (100., 1000.), (1500., 1900.))
	assert route == harbor_transit.plan(channel, (100., 1000.), (1500., 1900.))
	assert route[-1] == (1500., 1900.)
	legs = [(100., 1000.)] + route
	for (n0, e0), (n1, e1) in zip(legs, legs[1:]):
		for f in np.linspace(0., 1., 200):
			assert channel.sample(n0 + f * (n1 - n0), e0 + f * (e1 - e0)).bed < -6., 'a leg leaves the channel'
	assert 2 <= len(route) <= 4, 'one turn, not a staircase'


def test_an_end_out_of_reach_is_reported(channel):
	with pytest.raises(ValueError, match='no water below -6.0 m with 200.0 m clearance'):
		harbor_transit.plan(channel, (100., 1000.), (1500., 1900.), clear=200.)
	with pytest.raises(ValueError, match='no route through water below -6.0 m'):
		harbor_transit.plan(channel, (100., 1000.), (1990., 10.))


def test_seam_crossings_are_found_where_the_track_crosses_a_tile_edge():
	north = np.array([3990., 3999., 4001., 4010., 4010.])
	east = np.array([-10., -5., -1., 1., 5.])
	found = harbor_transit.crossings(north, east)
	assert [(k, axis, line) for k, axis, line, _ in found] == [(1, 'north', 4000.), (2, 'east', 0.)]
	assert found[0][3] == pytest.approx(.5)
	assert found[1][3] == pytest.approx(.5)


def csv(tmp_path, misses='0', holds='0', arrived='yes'):
	header = {'vessel': 't-class', 'environment_pack': 'harbor/2025-07-14', 'scenario_start_utc': '2025-07-14T16:00:00Z',
		'environment_current_field': 'station-blend', 'environment_depth_misses': misses, 'environment_time_holds': holds,
		'result.arrived': arrived, 'result.arrived_s': '100'}
	rows = ['time_s,north_m,east_m,depth_under_keel_m'] + [f'{t:.1f},{3900. + 2. * t:.3f},10.000,{8. - .001 * t:.3f}' for t in range(0, 101)]
	path = tmp_path / 'transit.csv'
	path.write_text(''.join(f'# {k}: {v}\n' for k, v in header.items()) + '\n'.join(rows) + '\n')
	return path


@pytest.mark.parametrize('changes,passed', [(dict(), True), (dict(misses='3'), False), (dict(holds='1'), False), (dict(arrived='no'), False)])
def test_a_transit_passes_only_arrived_without_depth_misses_or_time_holds(tmp_path, monkeypatch, changes, passed):
	# Two grids meeting at north 4000 with one bed: the track reaches their seam at row 50, after row 49.
	values = [[-10.] * 3] * 41
	depth = pack.load_depth(write_depth(tmp_path / 'depth', [('south', 3800., 0., 10., values[:21]), ('north', 4000., 0., 10., values[:21])]))
	summary = harbor_transit.check(csv(tmp_path, **changes), depth, tmp_path)
	assert summary['passed'] is passed
	assert [(s['row'], s['grid_before'], s['grid_after']) for s in summary['seams']] == [(49, 'south', 'north')]
	assert summary['seams'][0]['bed_step_2mm_m'] == 0.
	assert (tmp_path / 'transit.png').is_file()
