"""Channel-constrained current field: a static basis from the water's shape and the stations' positions.

On a world-frame lattice of water cells:

1. Flow pattern. Potential flow through the water (Laplace's equation for phi, fixed at the inflow and outflow edges,
   no flux through land): its velocity gives the local channel axis (flood-oriented, tangent to every shoreline) and a
   pattern speed S. The water's own geometry decides it; the bed does not (conveyance_exponent 0), so bed errors near
   structures cannot shut a landing's flow off.
2. Sheltering. min(1, (S / S_bar) / threshold), where S_bar is S smoothed through the water over about half the
   stations' spacing: water at least as fast as its surroundings is 1; slips, dead-end basins and the lee of land fall
   toward 0.
3. Depth. min(1, (h / h_bar) ^ gamma), h_bar the depth smoothed over a few hundred metres: water shallower than its
   surroundings runs slower, deeper water is not sped up past the stations' values.
4. Shore. Cells next to land have their axis turned onto the shoreline's tangent, and the part of the flow across the
   axis grows from 0 in those cells to all of it at cross_taper_m from the shore, so there is no normal flow at the
   shoreline while the flow along it keeps its strength.
   along = sheltering * depth, raised toward 1 within station_reach_m of a station so a station's value is never
   rescaled;
   cross = along * taper * (1 on a station's disk, falling to 0 at across_reach_m from it). A station's direction off
   the channel axis is its own: between stations the flow follows the axis.
5. Weights. Harmonic interpolation through the water only (Laplace's equation with each station a small disk held at 1
   for itself and 0 for the others, no flux through land or the edges): along a channel it blends the stations on either
   side by distance along it, and it never reaches across land. Kept to the largest few per cell, renormalised.

A station's velocity is resolved along and across its own frame (the local axis at the station); a cell blends those
components with its weights and rebuilds the velocity on its own axis, scaled by along and cross. A point blends the
four cells around it bilinearly, over the wet ones only (renormalised while they carry at least half the weight, fading
to zero over land beyond that). At a station that is the station's velocity (unless the station is so near the shore
that the taper cuts its across part), and between stations the flow turns with the channel.

Everything is deterministic: fixed scan orders, pairwise numpy sums, no threads, stations sorted by id.
"""
import heapq
from collections import deque
from dataclasses import dataclass

import numpy as np

EMPTY = 255
EDGES = ('south', 'north', 'west', 'east')
# The share of a point's bilinear weight its wet cells must carry for the blend to be renormalised in full.
WET_FLOOR = .5


@dataclass(frozen=True)
class Lattice:
	"""Cell centres at north = origin_north + r * cell and east = origin_east + c * cell; rows grow north."""
	origin_north: float
	origin_east: float
	cell: float
	rows: int
	cols: int

	def north(self):
		return self.origin_north + np.arange(self.rows) * self.cell

	def east(self):
		return self.origin_east + np.arange(self.cols) * self.cell


@dataclass(frozen=True)
class Station:
	id: str
	north: float
	east: float


@dataclass(frozen=True)
class Parameters:
	inflow: str = 'south'
	outflow: str = 'north'
	station_disk_m: float = 40.
	conveyance_exponent: float = 0.
	min_depth_m: float = 1.
	smoothing_m: float = 800.
	shelter_threshold: float = 1.
	depth_exponent: float = 1. / 6.
	depth_smoothing_m: float = 200.
	cross_taper_m: float = 60.
	station_reach_m: float = 120.
	across_reach_m: float = 500.
	slots: int = 6
	tolerance: float = 1e-10


@dataclass
class Basis:
	lattice: Lattice
	parameters: Parameters
	stations: list
	wet: np.ndarray
	depth: np.ndarray
	pattern_north: np.ndarray
	pattern_east: np.ndarray
	axis_north: np.ndarray
	axis_east: np.ndarray
	shelter: np.ndarray
	shore_m: np.ndarray
	along: np.ndarray
	cross: np.ndarray
	disks: np.ndarray
	weights: np.ndarray
	slots: np.ndarray
	slot_weights: np.ndarray
	frames: np.ndarray
	iterations: dict


def largest_component(water):
	"""The largest 4-connected set of water cells (the first found in row-major order on a tie)."""
	rows, cols = water.shape
	label = np.zeros(water.shape, dtype=np.int64)
	best, best_size, current = 0, 0, 0
	for r0, c0 in zip(*np.nonzero(water)):
		if label[r0, c0]:
			continue
		current += 1
		label[r0, c0] = current
		queue, size = deque([(r0, c0)]), 0
		while queue:
			r, c = queue.popleft()
			size += 1
			for rr, cc in ((r - 1, c), (r + 1, c), (r, c - 1), (r, c + 1)):
				if 0 <= rr < rows and 0 <= cc < cols and water[rr, cc] and not label[rr, cc]:
					label[rr, cc] = current
					queue.append((rr, cc))
		if size > best_size:
			best, best_size = current, size
	return label == best if best else np.zeros(water.shape, bool)


class WaterGraph:
	"""The 5-point operator on wet cells, with face conductances the harmonic mean of the two cells' coefficients."""

	def __init__(self, wet, coefficient):
		rows, cols = wet.shape
		self.wet = wet
		self.index = np.full(wet.shape, -1, dtype=np.int64)
		self.r, self.c = np.nonzero(wet)
		self.index[self.r, self.c] = np.arange(len(self.r))
		k = coefficient[self.r, self.c]
		self.neighbours, self.conductance = [], []
		for dr, dc in ((0, 1), (0, -1), (1, 0), (-1, 0)):
			rr, cc = self.r + dr, self.c + dc
			inside = (rr >= 0) & (rr < rows) & (cc >= 0) & (cc < cols)
			j = np.full(len(self.r), -1, dtype=np.int64)
			j[inside] = self.index[rr[inside], cc[inside]]
			kj = np.where(j >= 0, k[np.maximum(j, 0)], 0.)
			with np.errstate(divide='ignore', invalid='ignore'):
				g = np.where((j >= 0) & (k > 0) & (kj > 0), 2. * k * kj / (k + kj), 0.)
			self.neighbours.append(np.maximum(j, 0))
			self.conductance.append(g)
		self.diagonal = self.conductance[0] + self.conductance[1] + self.conductance[2] + self.conductance[3]

	def apply(self, x):
		y = self.diagonal * x
		for j, g in zip(self.neighbours, self.conductance):
			y = y - g * x[..., j]
		return y

	def solve(self, fixed, values, tolerance):
		"""x equal to values on fixed cells, A x = 0 on the others; values has shape (right-hand sides, cells).
		Jacobi-preconditioned conjugate gradients, all right-hand sides together. Returns x and the iterations."""
		x = np.array(values, dtype=float, copy=True)
		free = ~fixed
		inverse = np.where(free & (self.diagonal > 0), 1. / np.where(self.diagonal > 0, self.diagonal, 1.), 0.)
		r = -self.apply(x)
		r[..., fixed] = 0.
		start = np.sqrt(np.sum(r * r, axis=-1))
		z = r * inverse
		p = z.copy()
		rz = np.sum(r * z, axis=-1)
		for iteration in range(1, 200001):
			if np.all(np.sqrt(np.sum(r * r, axis=-1)) <= tolerance * np.maximum(start, 1e-300)):
				return x, iteration - 1
			ap = self.apply(p)
			ap[..., fixed] = 0.
			pap = np.sum(p * ap, axis=-1)
			alpha = np.where(pap > 0, rz / np.where(pap > 0, pap, 1.), 0.)
			x += alpha[..., None] * p
			r -= alpha[..., None] * ap
			z = r * inverse
			rz_next = np.sum(r * z, axis=-1)
			beta = np.where(rz > 0, rz_next / np.where(rz > 0, rz, 1.), 0.)
			p = z + beta[..., None] * p
			rz = rz_next
		raise RuntimeError('conjugate gradients did not converge')

	def smooth(self, values, length_cells, tolerance):
		"""(I + L^2 Lap) y = values with unit conductances between wet neighbours: a smoothing over about
		length_cells that never crosses land."""
		lam = float(length_cells) ** 2
		unit = [np.where(g > 0, 1., 0.) for g in self.conductance]
		degree = unit[0] + unit[1] + unit[2] + unit[3]

		def apply(x):
			y = x + lam * degree * x
			for j, u in zip(self.neighbours, unit):
				y = y - lam * u * x[j]
			return y

		x = np.array(values, dtype=float, copy=True)
		r = values - apply(x)
		inverse = 1. / (1. + lam * degree)
		start = np.sqrt(np.sum(r * r))
		z = r * inverse
		p = z.copy()
		rz = np.sum(r * z)
		for iteration in range(200000):
			if np.sqrt(np.sum(r * r)) <= tolerance * max(start, 1e-300):
				return x
			ap = apply(p)
			alpha = rz / np.sum(p * ap)
			x += alpha * p
			r -= alpha * ap
			z = r * inverse
			rz_next = np.sum(r * z)
			p = z + (rz_next / rz) * p
			rz = rz_next
		raise RuntimeError('smoothing did not converge')


def edge_cells(wet, edge):
	mask = np.zeros(wet.shape, bool)
	if edge == 'south':
		mask[0, :] = wet[0, :]
	elif edge == 'north':
		mask[-1, :] = wet[-1, :]
	elif edge == 'west':
		mask[:, 0] = wet[:, 0]
	elif edge == 'east':
		mask[:, -1] = wet[:, -1]
	else:
		raise ValueError(f'edge must be one of {EDGES}, found {edge!r}')
	return mask


def pattern(lattice, wet, depth, parameters):
	"""Cell velocities (north, east) of the potential flow from the inflow edge to the outflow edge, with face
	conductances depth ^ conveyance_exponent (0: plain potential flow)."""
	inflow, outflow = edge_cells(wet, parameters.inflow), edge_cells(wet, parameters.outflow)
	if not inflow.any() or not outflow.any():
		raise ValueError(f'the water must reach both the {parameters.inflow} and {parameters.outflow} edges')
	conveyance = np.where(wet, depth ** parameters.conveyance_exponent, 0.)
	graph = WaterGraph(wet, conveyance)
	fixed = (inflow | outflow)[graph.r, graph.c]
	values = np.where(inflow[graph.r, graph.c], 1., 0.)[None, :]
	phi_wet, iterations = graph.solve(fixed, values, parameters.tolerance)
	phi = np.zeros(wet.shape)
	phi[graph.r, graph.c] = phi_wet[0]
	# Face fluxes per unit width, positive east and north, from the same conductances the solve used.
	east_face = conveyance_faces(conveyance, axis=1) * (phi[:, :-1] - phi[:, 1:]) / lattice.cell
	north_face = conveyance_faces(conveyance, axis=0) * (phi[:-1, :] - phi[1:, :]) / lattice.cell
	flux_east = cell_flux(east_face, wet, axis=1)
	flux_north = cell_flux(north_face, wet, axis=0)
	# Velocity is flux per unit width over depth for a depth-integrated flow (exponent 1 or more: Manning's 5/3 gives
	# speed growing as h^2/3); plain potential flow (exponent 0) is the flux itself.
	h = np.where(wet, depth, 1.) ** min(parameters.conveyance_exponent, 1.)
	return np.where(wet, flux_north / h, 0.), np.where(wet, flux_east / h, 0.), iterations


def conveyance_faces(conveyance, axis):
	a, b = (conveyance[:, :-1], conveyance[:, 1:]) if axis == 1 else (conveyance[:-1, :], conveyance[1:, :])
	with np.errstate(divide='ignore', invalid='ignore'):
		return np.where((a > 0) & (b > 0), 2. * a * b / (a + b), 0.)


def cell_flux(face, wet, axis):
	"""Mean of a cell's two faces along an axis (a face onto land carries none); a cell on the lattice edge uses its
	one inner face, so an open edge is not mistaken for a wall."""
	rows, cols = wet.shape
	low = np.zeros(wet.shape)
	high = np.zeros(wet.shape)
	if axis == 1:
		high[:, :-1] = face
		low[:, 1:] = face
		total = low + high
		count = np.full(wet.shape, 2.)
		count[:, 0] = count[:, -1] = 1.
	else:
		high[:-1, :] = face
		low[1:, :] = face
		total = low + high
		count = np.full(wet.shape, 2.)
		count[0, :] = count[-1, :] = 1.
	return total / count


def shore_distance(lattice, wet):
	"""Distance (m) from each wet cell centre to the nearest shoreline, taken halfway to the nearest dry cell centre
	(8-neighbour chamfer). Cells beyond the lattice edge are open water, not shore."""
	rows, cols = wet.shape
	distance = np.full(wet.shape, np.inf)
	heap = []
	steps = [(dr, dc, float(np.hypot(dr, dc)) * lattice.cell) for dr in (-1, 0, 1) for dc in (-1, 0, 1) if dr or dc]
	for r, c in zip(*np.nonzero(wet)):
		seed = min((step / 2. for dr, dc, step in steps
			if 0 <= r + dr < rows and 0 <= c + dc < cols and not wet[r + dr, c + dc]), default=np.inf)
		if seed < np.inf:
			distance[r, c] = seed
			heapq.heappush(heap, (seed, int(r), int(c)))
	while heap:
		d, r, c = heapq.heappop(heap)
		if d > distance[r, c]:
			continue
		for dr, dc, step in steps:
			rr, cc = r + dr, c + dc
			if 0 <= rr < rows and 0 <= cc < cols and wet[rr, cc] and d + step < distance[rr, cc]:
				distance[rr, cc] = d + step
				heapq.heappush(heap, (d + step, rr, cc))
	return distance


def harmonic_weights(wet, disks, tolerance):
	"""Each station's weight: 1 on its disk, 0 on the others', harmonic elsewhere in the water (no flux through land or
	the lattice edges); renormalised to sum to 1 against round-off."""
	graph = WaterGraph(wet, np.ones(wet.shape))
	fixed = disks.any(axis=0)[graph.r, graph.c]
	values = disks[:, graph.r, graph.c].astype(float)
	solved, iterations = graph.solve(fixed, values, tolerance)
	solved = np.clip(solved, 0., 1.)
	solved = solved / solved.sum(axis=0)
	weights = np.zeros((len(disks),) + wet.shape)
	weights[:, graph.r, graph.c] = solved
	return weights, iterations


def truncate(weights, slots):
	"""The largest `slots` weights of each cell (ties to the lower station index), renormalised to sum to 1.
	weights has shape (stations, ...); returns station indices (..., slots) as uint8 (EMPTY for none) and weights."""
	count = weights.shape[0]
	if count >= EMPTY:
		raise ValueError(f'at most {EMPTY - 1} stations fit a uint8 slot')
	moved = np.moveaxis(weights, 0, -1)
	# A stable sort on the negated weights keeps the lower index first among equal weights.
	order = np.argsort(-moved, axis=-1, kind='stable')[..., :slots]
	kept = np.take_along_axis(moved, order, axis=-1)
	total = kept.sum(axis=-1, keepdims=True)
	kept = np.where(total > 0, kept / np.where(total > 0, total, 1.), 0.)
	index = np.where(kept > 0, order, EMPTY).astype(np.uint8)
	if slots > count:
		pad = [(0, 0)] * (index.ndim - 1) + [(0, slots - count)]
		index = np.pad(index, pad, constant_values=EMPTY)
		kept = np.pad(kept, pad)
	return index, np.where(index == EMPTY, 0., kept)


@dataclass
class Geometry:
	"""What the field takes from the water's shape alone, before any station."""
	lattice: Lattice
	parameters: Parameters
	wet: np.ndarray
	depth: np.ndarray
	pattern_north: np.ndarray
	pattern_east: np.ndarray
	axis_north: np.ndarray
	axis_east: np.ndarray
	boundary: np.ndarray
	shelter: np.ndarray
	depth_ratio: np.ndarray
	shore_m: np.ndarray
	iterations: int


def geometry(lattice, water, bed, parameters):
	"""Pattern, axis, sheltering, relative depth and shore distance from a water mask and bed elevations (NAVD88 m,
	positive up)."""
	if water.shape != (lattice.rows, lattice.cols) or bed.shape != water.shape:
		raise ValueError('water and bed must match the lattice')
	wet = largest_component(water)
	depth = np.where(wet, np.maximum(-np.where(np.isfinite(bed), bed, 0.), parameters.min_depth_m), 0.)
	pattern_north, pattern_east, iterations = pattern(lattice, wet, depth, parameters)
	speed = np.hypot(pattern_north, pattern_east)
	axis_north = np.where(speed > 0, pattern_north / np.where(speed > 0, speed, 1.), 0.)
	axis_east = np.where(speed > 0, pattern_east / np.where(speed > 0, speed, 1.), 0.)
	axis_east = np.where(wet & (speed == 0), 1., axis_east)
	graph = WaterGraph(wet, np.ones(wet.shape))
	smoothed = np.zeros(wet.shape)
	smoothed[graph.r, graph.c] = graph.smooth(speed[graph.r, graph.c], parameters.smoothing_m / lattice.cell, parameters.tolerance)
	shelter = np.where(wet & (smoothed > 0), speed / np.where(smoothed > 0, smoothed, 1.), 0.)
	mean_depth = np.zeros(wet.shape)
	mean_depth[graph.r, graph.c] = graph.smooth(depth[graph.r, graph.c], parameters.depth_smoothing_m / lattice.cell, parameters.tolerance)
	depth_ratio = np.where(wet & (mean_depth > 0), depth / np.where(mean_depth > 0, mean_depth, 1.), 0.)
	axis_north, axis_east, boundary = along_the_shore(wet, np.where(wet, axis_north, 0.), np.where(wet, axis_east, 0.))
	return Geometry(lattice=lattice, parameters=parameters, wet=wet, depth=depth, pattern_north=pattern_north,
		pattern_east=pattern_east, axis_north=axis_north, axis_east=axis_east, boundary=boundary,
		shelter=shelter, depth_ratio=depth_ratio, shore_m=np.where(wet, shore_distance(lattice, wet), 0.), iterations=iterations)


def along_the_shore(wet, axis_north, axis_east):
	"""Turns the axis of every wet cell with a dry neighbour (of eight) onto the shoreline's tangent there. The shore
	normal points from the dry neighbours toward the cell; an axis square to it (flow onto a wall) takes the tangent
	turned clockwise from the normal. Returns the axes and the boundary mask."""
	rows, cols = wet.shape
	normal_north = np.zeros(wet.shape)
	normal_east = np.zeros(wet.shape)
	boundary = np.zeros(wet.shape, bool)
	padded = np.pad(wet, 1, mode='edge')  # a shore that reaches an open edge carries on past it
	for dr in (-1, 0, 1):
		for dc in (-1, 0, 1):
			if dr or dc:
				dry = ~padded[1 + dr:1 + dr + rows, 1 + dc:1 + dc + cols]
				length = np.hypot(dr, dc)
				normal_north -= np.where(dry, dr / length, 0.)
				normal_east -= np.where(dry, dc / length, 0.)
				boundary |= dry
	boundary &= wet
	size = np.hypot(normal_north, normal_east)
	# Shores on opposite sides (a channel one cell wide) cancel: the axis there already runs between them.
	turn = boundary & (size > 1e-9)
	nn = np.where(turn, normal_north / np.where(turn, size, 1.), 0.)
	ne = np.where(turn, normal_east / np.where(turn, size, 1.), 0.)
	dot = axis_north * nn + axis_east * ne
	tn, te = axis_north - dot * nn, axis_east - dot * ne
	length = np.hypot(tn, te)
	square = turn & (length < 1e-9)
	tn = np.where(square, -ne, tn)
	te = np.where(square, nn, te)
	length = np.where(square, 1., length)
	axis_north = np.where(turn, tn / np.where(length > 0, length, 1.), axis_north)
	axis_east = np.where(turn, te / np.where(length > 0, length, 1.), axis_east)
	return axis_north, axis_east, boundary


def with_stations(geometry, stations, solved=None):
	"""The basis for a set of stations on a geometry: disks, reach, factors and harmonic weights. `solved`, a basis for the
	same stations on the same water, lends its weights instead of solving them again (they depend on the water and the
	stations only)."""
	lattice, parameters, wet = geometry.lattice, geometry.parameters, geometry.wet
	start = lattice.cell / 2.
	if not (parameters.station_reach_m > parameters.station_disk_m and parameters.across_reach_m > parameters.station_disk_m):
		raise ValueError(f'station_reach_m and across_reach_m must exceed station_disk_m ({parameters.station_disk_m:g} m)')
	if not parameters.cross_taper_m > start:
		raise ValueError(f'cross_taper_m must exceed half a cell ({start:g} m)')
	stations = sorted(stations, key=lambda s: s.id)
	if len({s.id for s in stations}) != len(stations) or not stations:
		raise ValueError('stations must be present with unique ids')
	for s in stations:
		r, c = (s.north - lattice.origin_north) / lattice.cell, (s.east - lattice.origin_east) / lattice.cell
		near = int(round(r)), int(round(c))
		if not (0 <= near[0] < lattice.rows and 0 <= near[1] < lattice.cols) or not wet[near]:
			raise ValueError(f'station {s.id} at north {s.north:.1f}, east {s.east:.1f} is not on the connected water')
	for i, a in enumerate(stations):
		for b in stations[i + 1:]:
			if np.hypot(a.north - b.north, a.east - b.east) <= 2. * parameters.station_disk_m:
				raise ValueError(f'stations {a.id} and {b.id} are closer than two disk radii; their disks would overlap')
	north, east = np.meshgrid(lattice.north(), lattice.east(), indexing='ij')
	disks = np.zeros((len(stations),) + wet.shape, bool)
	reach = np.zeros(wet.shape)
	nearest = np.full(wet.shape, np.inf)
	span = parameters.station_reach_m - parameters.station_disk_m
	for k, s in enumerate(stations):
		distance = np.hypot(north - s.north, east - s.east)
		disks[k] = wet & (distance <= parameters.station_disk_m)
		reach = np.maximum(reach, np.clip((parameters.station_reach_m - distance) / span, 0., 1.))
		nearest = np.minimum(nearest, distance)
	reach = np.where(wet, reach, 0.)
	sheltering = np.minimum(1., geometry.shelter / parameters.shelter_threshold)
	shallowing = np.minimum(1., geometry.depth_ratio ** parameters.depth_exponent)
	along = np.maximum(np.where(wet, sheltering * shallowing, 0.), reach)
	# Zero on the cells next to the shore, all of it cross_taper_m from the shoreline (half a cell beyond their centres).
	taper = np.where(wet & ~geometry.boundary, np.clip((geometry.shore_m - start) / (parameters.cross_taper_m - start), 0., 1.), 0.)
	# A station's direction off the channel axis is its own: all of it on its disk, none from across_reach_m out.
	across = np.clip((parameters.across_reach_m - nearest) / (parameters.across_reach_m - parameters.station_disk_m), 0., 1.)
	cross = along * taper * across
	if solved is not None:
		if [s.id for s in solved.stations] != [s.id for s in stations] or not np.array_equal(solved.wet, wet) \
			or not np.array_equal(solved.disks, disks) or solved.parameters.slots != parameters.slots:
			raise ValueError('the solved weights are for other stations, water or disks')
		weights, slots, slot_weights, iterations = solved.weights, solved.slots, solved.slot_weights, solved.iterations['weights']
	else:
		weights, iterations = harmonic_weights(wet, disks, parameters.tolerance)
		slots, slot_weights = truncate(weights, parameters.slots)
		slots[~wet] = EMPTY
		slot_weights[~wet] = 0.
	basis = Basis(lattice=lattice, parameters=parameters, stations=stations, wet=wet, depth=geometry.depth,
		pattern_north=geometry.pattern_north, pattern_east=geometry.pattern_east, axis_north=geometry.axis_north,
		axis_east=geometry.axis_east, shelter=geometry.shelter, shore_m=geometry.shore_m, along=along, cross=cross,
		disks=disks, weights=weights, slots=slots, slot_weights=slot_weights, frames=None,
		iterations=dict(pattern=geometry.iterations, weights=iterations))
	basis.frames = np.array([frame(basis, s.north, s.east) for s in stations])
	return basis


def build(lattice, water, bed, stations, parameters):
	"""The basis on `lattice` from a water mask, bed elevations (NAVD88 m, positive up) and stations."""
	return with_stations(geometry(lattice, water, bed, parameters), stations)


def corners(lattice, north, east):
	"""The four cells around each point and their bilinear weights; points outside the lattice raise."""
	north, east = np.asarray(north, dtype=float), np.asarray(east, dtype=float)
	r = (north - lattice.origin_north) / lattice.cell
	c = (east - lattice.origin_east) / lattice.cell
	if np.any((r < 0) | (r > lattice.rows - 1) | (c < 0) | (c > lattice.cols - 1)):
		raise ValueError('a point lies outside the lattice')
	r0 = np.minimum(r.astype(np.int64), lattice.rows - 2)
	c0 = np.minimum(c.astype(np.int64), lattice.cols - 2)
	fr, fc = r - r0, c - c0
	return [(r0, c0, (1 - fr) * (1 - fc)), (r0, c0 + 1, (1 - fr) * fc), (r0 + 1, c0, fr * (1 - fc)), (r0 + 1, c0 + 1, fr * fc)]


def frame(basis, north, east):
	"""A station's frame: the cell axes around its position blended by the sampling rule (not renormalised to unit
	length, so the field there rebuilds the station's velocity exactly when its four cells are its own)."""
	return frame_on(basis.lattice, basis.axis_north, basis.axis_east, basis.slots[..., 0] != EMPTY, north, east)


def frame_on(lattice, axis_north, axis_east, wet, north, east):
	"""frame() on explicit arrays, such as a pack's stored float32 grid."""
	vector, total = np.zeros(2), 0.
	for r, c, w in corners(lattice, np.array([north]), np.array([east])):
		if wet[r[0], c[0]]:
			vector += w[0] * np.array([float(axis_north[r[0], c[0]]), float(axis_east[r[0], c[0]])])
			total += w[0]
	vector = vector / max(total, WET_FLOOR)
	if not np.hypot(*vector) > 0:
		raise ValueError(f'no flow axis at north {north:.1f}, east {east:.1f}')
	return vector


def components(frames, velocity):
	"""Each station's velocity (stations, ..., 2) resolved on its frame f: v = along f + across f_perp, f_perp = (-f_e, f_n)."""
	f = frames.reshape((frames.shape[0],) + (1,) * (velocity.ndim - 2) + (2,))
	scale = f[..., 0] ** 2 + f[..., 1] ** 2
	along = (velocity[..., 0] * f[..., 0] + velocity[..., 1] * f[..., 1]) / scale
	across = (-velocity[..., 0] * f[..., 1] + velocity[..., 1] * f[..., 0]) / scale
	return along, across


def evaluate(basis, velocities, north, east, axis_north=None, axis_east=None, along=None, cross=None, slots=None, slot_weights=None,
	wet_floor=WET_FLOOR):
	"""The field at points (north[i], east[i]) with each station's velocity at that point's time: velocities maps
	station id to an array (points, 2). The sampling rule: each of the four cells around the point gives its velocity
	(slot weights over the stations' along and across parts, rebuilt on its axis, times its factors); the point's value is
	their bilinear blend over the wet cells, divided by the wet cells' share of the weight or WET_FLOOR, whichever is
	larger. The optional arrays replace the basis's own (for the stored float32 copy); wet_floor 1 counts dry cells as
	zero instead (only for comparing the two rules)."""
	axis_north = basis.axis_north if axis_north is None else axis_north
	axis_east = basis.axis_east if axis_east is None else axis_east
	along = basis.along if along is None else along
	cross = basis.cross if cross is None else cross
	slots = basis.slots if slots is None else slots
	slot_weights = basis.slot_weights if slot_weights is None else slot_weights
	stacked = np.stack([np.asarray(velocities[s.id], dtype=float) for s in basis.stations])
	s_along, s_across = components(basis.frames, stacked)
	s_along = np.concatenate([s_along, np.zeros((1,) + s_along.shape[1:])])
	s_across = np.concatenate([s_across, np.zeros((1,) + s_across.shape[1:])])
	points = np.arange(len(np.atleast_1d(north)))
	result = np.zeros((len(points), 2))
	total = np.zeros(len(points))
	for r, c, w in corners(basis.lattice, north, east):
		wet = slots[r, c, 0] != EMPTY
		index = np.where(slots[r, c] == EMPTY, len(basis.stations), slots[r, c])
		a = np.sum(slot_weights[r, c] * s_along[index, points[:, None]], axis=-1)
		x = np.sum(slot_weights[r, c] * s_across[index, points[:, None]], axis=-1)
		an, ae = axis_north[r, c], axis_east[r, c]
		w = np.where(wet, w, 0.)
		result[:, 0] += w * (along[r, c] * a * an - cross[r, c] * x * ae)
		result[:, 1] += w * (along[r, c] * a * ae + cross[r, c] * x * an)
		total += w
	# Renormalised over the wet cells while they carry at least WET_FLOOR of the weight; fading to zero over land.
	return result / np.maximum(total, wet_floor)[:, None]
