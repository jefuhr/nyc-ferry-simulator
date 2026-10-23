#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.12"
# dependencies = ["matplotlib"]
# ///
"""Plot a ferrysim telemetry CSV: track with heading ticks, speed and heading, levers, rudders.

Usage: uv run tools/plot_maneuver.py <csv> --out <png>
"""

from __future__ import annotations

import argparse
import csv
import math
import sys
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402

# Categorical slots 1 and 2 of the reference palette, plus muted ink for context.
BLUE = "#2a78d6"
ORANGE = "#eb6834"
INK = "#0b0b0b"
INK_MUTED = "#52514e"
GRID = "#d9d8d3"
SURFACE = "#fcfcfb"

TEXT_COLUMNS = {"port_gear", "stbd_gear"}


def read_telemetry(path: Path) -> tuple[dict[str, str], dict[str, list[float]]]:
	header: dict[str, str] = {}
	body: list[str] = []
	for line in path.read_text(encoding="utf-8").splitlines():
		if line.startswith("#"):
			key, sep, value = line[1:].partition(":")
			if sep:
				header[key.strip()] = value.strip()
		elif line.strip():
			body.append(line)
	if "ferrysim-csv" not in header:
		raise SystemExit(f"{path}: not a ferrysim telemetry CSV (no '# ferrysim-csv:' header comment)")
	columns: dict[str, list[float]] = {}
	for row in csv.DictReader(body):
		for name, cell in row.items():
			if name not in TEXT_COLUMNS:
				columns.setdefault(name, []).append(float(cell))
	if not columns.get("time_s"):
		raise SystemExit(f"{path}: no data rows")
	return header, columns


def unwrap_degrees(values: list[float]) -> list[float]:
	"""Heading change from the first sample, without the 360 degree jumps."""
	out = [0.0]
	for previous, current in zip(values, values[1:]):
		step = (current - previous + 180.0) % 360.0 - 180.0
		out.append(out[-1] + step)
	return out


def nice_interval(duration: float, target_ticks: int = 30) -> float:
	raw = max(duration / target_ticks, 0.1)
	magnitude = 10 ** math.floor(math.log10(raw))
	for factor in (1, 2, 5, 10):
		if raw <= factor * magnitude:
			return factor * magnitude
	return 10 * magnitude


def style_axes(ax, ylabel: str) -> None:
	ax.set_facecolor(SURFACE)
	ax.set_ylabel(ylabel, color=INK_MUTED)
	ax.grid(True, color=GRID, linewidth=0.6)
	ax.tick_params(colors=INK_MUTED, labelsize=8)
	for side in ("top", "right"):
		ax.spines[side].set_visible(False)
	for side in ("left", "bottom"):
		ax.spines[side].set_color(GRID)


def plot(header: dict[str, str], data: dict[str, list[float]], out: Path, dpi: int) -> None:
	t = data["time_s"]
	north, east = data["north_m"], data["east_m"]
	heading = data["heading_deg"]
	start = float(header.get("maneuver_start_s", t[0]))
	rel_t = [x - start for x in t]
	started = [i for i, x in enumerate(t) if x >= start - 1e-6]
	first = started[0] if started else 0

	fig = plt.figure(figsize=(13, 7.5), facecolor=SURFACE)
	grid = fig.add_gridspec(4, 2, width_ratios=[1.15, 1.0], hspace=0.12, wspace=0.18, left=0.06, right=0.98, top=0.88, bottom=0.07)

	# Track: east on x, north up, equal scale so a circle looks like a circle.
	track = fig.add_subplot(grid[:, 0])
	style_axes(track, "north (m)")
	track.set_xlabel("east (m)", color=INK_MUTED)
	if first > 0:
		track.plot(east[: first + 1], north[: first + 1], color=INK_MUTED, linewidth=1.2, linestyle=":", label="before maneuver")
	track.plot(east[first:], north[first:], color=BLUE, linewidth=2.0, label="track")
	extent = max(max(east) - min(east), max(north) - min(north), 1.0)
	tick_length = 0.06 * extent
	interval = nice_interval(t[-1] - t[first], 24)
	next_tick = t[first]
	for i in range(first, len(t)):
		if t[i] >= next_tick - 1e-9:
			rad = math.radians(heading[i])
			track.annotate(
				"",
				xy=(east[i] + tick_length * math.sin(rad), north[i] + tick_length * math.cos(rad)),
				xytext=(east[i], north[i]),
				arrowprops={"arrowstyle": "-|>", "color": ORANGE, "linewidth": 1.4, "shrinkA": 0, "shrinkB": 0, "mutation_scale": 9},
			)
			next_tick += interval
	track.plot([], [], color=ORANGE, linewidth=1.4, label=f"heading, every {interval:g} s")
	track.scatter([east[first]], [north[first]], s=60, color=BLUE, edgecolor=SURFACE, linewidth=2, zorder=5)
	track.scatter([east[-1]], [north[-1]], s=60, color=INK, edgecolor=SURFACE, linewidth=2, zorder=5)
	track.annotate("start", (east[first], north[first]), textcoords="offset points", xytext=(8, 8), color=INK, fontsize=9)
	track.annotate("end", (east[-1], north[-1]), textcoords="offset points", xytext=(8, -14), color=INK, fontsize=9)
	track.set_aspect("equal", adjustable="datalim")
	track.margins(0.08)
	track.legend(loc="best", frameon=False, labelcolor=INK_MUTED, fontsize=8)

	def strip(row: int, ylabel: str, share=None):
		ax = fig.add_subplot(grid[row, 1], sharex=share)
		style_axes(ax, ylabel)
		if start > t[0]:
			ax.axvline(0.0, color=INK_MUTED, linewidth=0.9, linestyle="--")
		return ax

	speed = strip(0, "speed (kn)")
	speed.plot(rel_t, data["stw_kn"], color=BLUE, linewidth=2.0, label="through water")
	speed.plot(rel_t, data["sog_kn"], color=ORANGE, linewidth=2.0, linestyle="--", label="over ground")
	speed.legend(loc="best", frameon=False, labelcolor=INK_MUTED, fontsize=8)

	hdg = strip(1, "heading chg (deg)", speed)
	hdg.plot(rel_t, unwrap_degrees(heading), color=BLUE, linewidth=2.0)

	levers = strip(2, "lever (-1 to +1)", speed)
	levers.step(rel_t, data["port_lever"], where="post", color=BLUE, linewidth=2.0, label="port")
	levers.step(rel_t, data["starboard_lever"], where="post", color=ORANGE, linewidth=2.0, linestyle="--", label="starboard")
	levers.set_ylim(-1.1, 1.1)
	levers.legend(loc="best", frameon=False, labelcolor=INK_MUTED, fontsize=8, ncols=2)

	rudder = strip(3, "rudder (deg, + stbd)", speed)
	rudder.plot(rel_t, data["port_rudder_deg"], color=BLUE, linewidth=2.0, label="port")
	rudder.plot(rel_t, data["stbd_rudder_deg"], color=ORANGE, linewidth=2.0, linestyle="--", label="starboard")
	rudder.legend(loc="best", frameon=False, labelcolor=INK_MUTED, fontsize=8, ncols=2)
	rudder.set_xlabel("time since maneuver start (s)", color=INK_MUTED)
	for ax in (speed, hdg, levers):
		ax.tick_params(labelbottom=False)

	params = ", ".join(f"{k[6:]}={v}" for k, v in header.items() if k.startswith("param."))
	fig.suptitle(
		f"{header.get('vessel', '?')}: {header.get('maneuver', '?')}",
		x=0.06, y=0.965, ha="left", fontsize=15, color=INK, fontweight="bold",
	)
	fig.text(
		0.06, 0.915,
		f"{params}  |  environment: {header.get('environment', '?')}  |  model: {header.get('model', '?').rsplit('.', 1)[-1]}",
		ha="left", fontsize=9, color=INK_MUTED,
	)
	out.parent.mkdir(parents=True, exist_ok=True)
	fig.savefig(out, dpi=dpi, facecolor=SURFACE)
	plt.close(fig)


def main(argv: list[str]) -> int:
	parser = argparse.ArgumentParser(description="Plot a ferrysim telemetry CSV to a PNG.")
	parser.add_argument("csv", type=Path, help="telemetry CSV written by ferrysim")
	parser.add_argument("--out", type=Path, help="PNG to write (default: the CSV path with a .png suffix)")
	parser.add_argument("--dpi", type=int, default=130)
	args = parser.parse_args(argv)
	if not args.csv.is_file():
		print(f"plot_maneuver: CSV not found: {args.csv}", file=sys.stderr)
		return 1
	out = args.out or args.csv.with_suffix(".png")
	header, data = read_telemetry(args.csv)
	plot(header, data, out, args.dpi)
	print(f"wrote {out}")
	return 0


if __name__ == "__main__":
	sys.exit(main(sys.argv[1:]))
