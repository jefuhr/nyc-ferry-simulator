"""The channel-constrained current field on synthetic water. Failure modes guarded:

- a station's influence crossing land into a neighbouring channel (what inverse distance does);
- weights that are not a partition of unity, are negative, or do not equal 1 on a station's own disk;
- interpolation that is not along the channel (linear in distance along it between two stations);
- an axis that is not tangent to the shore, or a cross-channel part that survives at the shoreline (normal flow), even
  next to a station;
- a station's own direction off the channel axis carried far from it, turning the flow off the axis between stations;
- a dead-end basin or slip that keeps the open channel's speed;
- the field not reproducing a station's velocity at the station, or not following a bend between stations;
- order dependence or nondeterminism (two builds differ, or station order changes the result);
- truncation to a few stations per cell that does not renormalise, or ties broken by accident;
- stations off the water, or so close that their disks overlap, and reaches or tapers that break the factors.
"""
import numpy as np
import pytest

from ferrydata.environment import field


def lattice(rows, cols, cell=20.):
	return field.Lattice(origin_north=0., origin_east=0., cell=cell, rows=rows, cols=cols)


def channel(rows=12, cols=60, cell=20.):
	"""An east-west channel 10 cells wide between two straight banks (rows 0 and rows-1 are land)."""
	water = np.zeros((rows, cols), bool)
	water[1:-1, :] = True
	return lattice(rows, cols, cell), water


def params(**overrides):
	return field.Parameters(**{**dict(inflow='west', outflow='east'), **overrides})


def station(sid, north, east):
	return field.Station(sid, north, east)


def test_stations_off_the_water_or_with_overlapping_disks_are_refused():
	lat, water = channel()
	bed = np.full(water.shape, -10.)
	with pytest.raises(ValueError, match='S1.*water'):
		field.build(lat, water, bed, [station('S1', 0., 200.)], params())
	with pytest.raises(ValueError, match='overlap'):
		field.build(lat, water, bed, [station('S1', 110., 200.), station('S2', 110., 250.)], params())


def test_weights_are_a_partition_of_unity_and_one_on_their_own_disk():
	lat, water = channel()
	bed = np.full(water.shape, -10.)
	stations = [station('A', 110., 200.), station('B', 110., 900.)]
	basis = field.build(lat, water, bed, stations, params())
	w = basis.weights
	assert np.all(w >= 0.) and np.all(w <= 1.)
	assert np.allclose(w.sum(axis=0)[basis.wet], 1., atol=1e-9)
	for k, s in enumerate(basis.stations):
		disk = basis.disks[k]
		assert disk.sum() >= 4
		assert np.all(w[k][disk] == 1.)
		assert np.all(w[1 - k][disk] == 0.)


def test_between_two_stations_in_a_channel_the_weights_are_linear_along_it():
	lat, water = channel(rows=8, cols=100)
	bed = np.full(water.shape, -10.)
	basis = field.build(lat, water, bed, [station('A', 70., 200.), station('B', 70., 1800.)], params(station_disk_m=30.))
	r = 3
	east = lat.origin_east + np.arange(lat.cols) * lat.cell
	inside = (east > 300.) & (east < 1700.)
	w = basis.weights[0][r, inside]
	# Disks are points at the channel's scale only when the channel is narrow: here it is 6 cells, so close to linear.
	slope = np.polyfit(east[inside], w, 1)
	assert slope[0] == pytest.approx(-1. / 1600., rel=0.08)
	assert np.max(np.abs(np.polyval(slope, east[inside]) - w)) < 0.05


def two_channels():
	"""Two parallel east-west channels separated by a 2-cell strip of land, joined at their west end."""
	rows, cols = 22, 80
	water = np.zeros((rows, cols), bool)
	water[1:10, :] = True       # south channel
	water[12:21, :] = True      # north channel
	water[1:21, :4] = True      # they meet at the west end
	return lattice(rows, cols), water


def test_a_station_does_not_reach_across_land_into_the_next_channel():
	lat, water = two_channels()
	bed = np.full(water.shape, -10.)
	stations = [station('SOUTH', 100., 1400.), station('NORTH', 320., 1400.)]
	basis = field.build(lat, water, bed, stations, params(inflow='west', outflow='east'))
	# In the north channel, 240 m from the south station across a 40 m strip of land but nearly 3 km from it by water.
	r, c = 12, 60
	assert basis.wet[r, c] and not basis.disks[:, r, c].any()
	ids = [s.id for s in basis.stations]
	assert basis.weights[ids.index('SOUTH')][r, c] < 0.1
	# Inverse distance squared would have given the south station almost half.
	d = np.hypot(np.array([s.north for s in stations]) - r * 20., np.array([s.east for s in stations]) - c * 20.)
	idw = (1 / d ** 2) / np.sum(1 / d ** 2)
	assert idw[[s.id for s in stations].index('SOUTH')] > 0.4


def test_the_axis_is_tangent_to_straight_banks_and_the_cross_part_vanishes_at_them():
	lat, water = channel()
	bed = np.full(water.shape, -10.)
	basis = field.build(lat, water, bed, [station('A', 110., 600.)], params())
	bank = basis.wet & ~np.roll(water, 1, axis=0)  # wet cells with land to the south
	assert bank.sum() > 10
	assert np.allclose(basis.axis_north[bank], 0., atol=1e-6)
	assert np.allclose(np.abs(basis.axis_east[bank]), 1., atol=1e-6)
	# Flood runs from the inflow edge (west) to the outflow edge (east).
	assert np.all(basis.axis_east[basis.wet] > 0.99)
	assert np.all(basis.cross[bank] < basis.along[bank] * 0.5)


def test_a_dead_end_basin_is_sheltered_and_the_open_channel_is_not():
	rows, cols = 40, 80
	water = np.zeros((rows, cols), bool)
	water[1:11, :] = True        # channel, west to east
	water[11:35, 30:36] = True   # a slip 6 cells wide and 24 long opening north off the channel
	lat = lattice(rows, cols)
	bed = np.full(water.shape, -10.)
	basis = field.build(lat, water, bed, [station('A', 110., 200.)], params())
	assert np.median(basis.along[5, 10:70]) == 1.
	assert np.max(basis.along[25:35, 30:36]) < 0.1


def series_for(stations, times):
	"""Different, reversing velocities per station (north, east), m/s."""
	out = {}
	for k, s in enumerate(stations):
		phase = 0.3 * k
		speed = (1. + 0.2 * k) * np.sin(2 * np.pi * times / 44712. + phase)
		direction = np.radians(80. + 10. * k)
		out[s.id] = np.stack([speed * np.cos(direction), speed * np.sin(direction)], axis=-1)
	return out


def test_at_a_station_the_field_is_that_station():
	lat, water = channel(rows=14, cols=80)
	bed = np.full(water.shape, -10.)
	stations = [station('A', 130., 300.), station('B', 130., 1300.)]
	basis = field.build(lat, water, bed, stations, params())
	times = np.arange(0., 44712., 900.)
	velocities = series_for(stations, times)
	for s in stations:
		u = field.evaluate(basis, velocities, np.full(times.shape, s.north), np.full(times.shape, s.east))
		assert u == pytest.approx(velocities[s.id], abs=2e-3)


def off_axis(degrees, speed=1.):
	"""One velocity sample (north, east) `degrees` north of east: off the axis of an east-running channel."""
	return np.array([[speed * np.sin(np.radians(degrees)), speed * np.cos(np.radians(degrees))]])


def test_away_from_stations_the_flow_follows_the_channel_axis():
	lat, water = channel(rows=14, cols=100)
	bed = np.full(water.shape, -10.)
	stations = [station('A', 120., 300.), station('B', 120., 1700.)]
	basis = field.build(lat, water, bed, stations, params(across_reach_m=200.))
	velocities = {'A': off_axis(30.), 'B': off_axis(0.)}
	# Midway, 700 m from both: A's along part still counts, but its 30 degrees off the axis stays at A.
	u = field.evaluate(basis, velocities, np.array([120.]), np.array([1000.]))[0]
	assert u[1] > 0.5
	assert abs(u[0]) < 1e-6
	u = field.evaluate(basis, velocities, np.array([120.]), np.array([300.]))[0]
	assert u == pytest.approx(velocities['A'][0], abs=1e-9)


def test_a_station_s_across_part_reaches_across_reach_m_and_no_further():
	lat, water = channel(rows=14, cols=100)
	bed = np.full(water.shape, -10.)
	basis = field.build(lat, water, bed, [station('A', 120., 1000.)], params(across_reach_m=240.))
	row = basis.cross[6]  # 120 m north, more than the shore taper from either bank
	distance = np.abs(lat.east() - 1000.)
	assert np.all(row[distance <= 40.] == 1.)
	assert row[distance == 140.] == pytest.approx([0.5, 0.5])
	assert np.all(row[distance >= 240.] == 0.)


def test_next_to_a_station_the_shoreline_still_has_no_normal_flow():
	lat, water = channel(rows=14, cols=60)
	bed = np.full(water.shape, -10.)
	# 20 m from the southern shoreline (north 10 m): inside the shore taper, and its reach covers the bank.
	basis = field.build(lat, water, bed, [station('A', 30., 600.)], params())
	bank = np.zeros(water.shape, bool)
	bank[1, np.abs(lat.east() - 600.) <= 100.] = True
	assert np.all(basis.wet[bank]) and np.all(basis.shore_m[bank] < lat.cell)
	assert np.all(basis.cross[bank] == 0.)
	u = field.evaluate(basis, {'A': np.repeat(off_axis(30.), 5, axis=0)}, np.full(5, 10.), np.linspace(560., 640., 5))
	assert np.all(u[:, 1] > 0.5)
	assert np.all(np.abs(u[:, 0]) < 1e-6)


@pytest.mark.parametrize('change', [dict(across_reach_m=40.), dict(station_reach_m=40.), dict(cross_taper_m=10.)])
def test_reaches_and_tapers_that_break_the_factors_are_refused(change):
	lat, water = channel()
	bed = np.full(water.shape, -10.)
	with pytest.raises(ValueError, match=next(iter(change))):
		field.build(lat, water, bed, [station('A', 110., 600.)], params(**change))


def bend():
	"""A channel 8 cells wide that runs north, turns 90 degrees and runs east."""
	rows, cols = 60, 60
	water = np.zeros((rows, cols), bool)
	water[0:50, 2:10] = True
	water[42:50, 2:60] = True
	return lattice(rows, cols), water


def test_the_field_follows_a_bend_between_two_stations():
	lat, water = bend()
	bed = np.full(water.shape, -10.)
	stations = [station('S', 200., 120.), station('E', 920., 1000.)]
	basis = field.build(lat, water, bed, stations, params(inflow='south', outflow='east'))
	t = np.array([11178.])
	velocities = {'S': np.array([[1., 0.]]), 'E': np.array([[0., 1.]])}
	# Halfway down the southern leg the flow still runs north, not toward the east station's direction.
	u = field.evaluate(basis, velocities, np.array([600.]), np.array([120.]))[0]
	assert np.degrees(np.arctan2(u[1], u[0])) == pytest.approx(0., abs=5.)
	# At the outside of the corner it runs diagonally, along the bend.
	u = field.evaluate(basis, velocities, np.array([920.]), np.array([120.]))[0]
	assert 10. < np.degrees(np.arctan2(u[1], u[0])) < 80.


def test_two_builds_are_identical_and_station_order_does_not_matter():
	lat, water = two_channels()
	bed = np.full(water.shape, -10.)
	bed[5, 30:40] = -2.
	stations = [station('SOUTH', 100., 1400.), station('NORTH', 320., 1400.), station('WEST', 220., 20.)]
	a = field.build(lat, water, bed, stations, params())
	b = field.build(lat, water, bed, stations, params())
	c = field.build(lat, water, bed, list(reversed(stations)), params())
	for name in ('axis_north', 'axis_east', 'along', 'cross'):
		assert np.array_equal(getattr(a, name), getattr(b, name))
		assert np.array_equal(getattr(a, name), getattr(c, name))
	order = [s.id for s in c.stations]
	assert order == sorted(order)
	assert np.array_equal(a.weights, c.weights)


def test_truncation_keeps_the_largest_weights_renormalised_with_ties_by_station_order():
	w = np.zeros((5, 1, 3))
	w[:, 0, 0] = [0.1, 0.2, 0.3, 0.25, 0.15]
	w[:, 0, 1] = [0.25, 0.25, 0.25, 0.25, 0.]
	w[:, 0, 2] = [0., 0., 1., 0., 0.]
	slots, weights = field.truncate(w, 4)
	assert slots[0, 0].tolist() == [2, 3, 1, 4]
	assert weights[0, 0] == pytest.approx(np.array([0.3, 0.25, 0.2, 0.15]) / 0.9)
	assert slots[0, 1].tolist() == [0, 1, 2, 3]
	assert slots[0, 2].tolist() == [2, field.EMPTY, field.EMPTY, field.EMPTY]
	assert weights[0, 2].tolist() == [1., 0., 0., 0.]


def test_flow_along_a_bank_keeps_its_strength_up_to_the_shoreline_and_fades_over_land():
	lat, water = channel(rows=14, cols=60)
	bed = np.full(water.shape, -10.)
	basis = field.build(lat, water, bed, [station('A', 130., 600.)], params())
	velocities = {'A': np.array([[0., 1.]] * 5)}
	east = np.full(5, 600.)
	# Row 1 is the last wet row (centre 20 m north), row 0 dry land (centre 0 m): the shoreline is between them at 10 m.
	north = np.array([20., 10., 5., 0.001, 0.])
	u = field.evaluate(basis, velocities, north, east)
	assert u[0, 1] > 0.9
	assert u[1, 1] == pytest.approx(u[0, 1]), 'at the shoreline the bank cell is renormalised in full'
	assert u[2, 1] == pytest.approx(u[0, 1] * 0.25 / field.WET_FLOOR), 'over land it fades'
	assert u[3, 1] == pytest.approx(0., abs=1e-3), 'and is continuous down to the dry centre'
	assert np.all(u[4] == 0.)


def staircase():
	"""Water south-east of a diagonal shore running from south-west to north-east."""
	rows, cols = 40, 40
	r, c = np.mgrid[0:rows, 0:cols]
	return lattice(rows, cols), c > r + 2


def test_a_diagonal_shore_has_no_normal_flow_in_its_boundary_cells():
	lat, water = staircase()
	bed = np.full(water.shape, -10.)
	basis = field.build(lat, water, bed, [station('A', 200., 500.)], params(inflow='south', outflow='east'))
	velocities = {'A': np.array([[0.6, 0.8]])}
	normal = np.array([-1., 1.]) / np.sqrt(2.)  # toward the water, away from the shore
	boundary = basis.wet & (basis.shore_m < lat.cell)  # the cells with a dry neighbour, half a cell or a half-diagonal out
	assert np.all(basis.cross[boundary] == 0.)
	inner = boundary & (np.arange(lat.rows)[:, None] > 4) & (np.arange(lat.cols)[None, :] < lat.cols - 5)
	assert inner.sum() > 20
	for r, c in zip(*np.nonzero(inner)):
		u = field.evaluate(basis, velocities, np.array([r * 20.]), np.array([c * 20.]))[0]
		assert abs(u @ normal) < 1e-9 * max(1., np.hypot(*u)), (r, c, u)
