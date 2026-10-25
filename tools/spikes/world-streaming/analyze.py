"""Turns the spike's run folders into the report's tables and figures.

	uv run --project data-pipeline python -I tools/spikes/world-streaming/analyze.py

Reads artifacts/world-streaming-spike/runs/<run>/ (frames.csv, memory.csv, events.csv, summary.json, rss.csv,
machine.txt) and writes artifacts/world-streaming-spike/analysis/: tables.md, runs.csv, hitches.csv and PNG figures.

Frame statistics use the moving phase only and leave out the frames flagged for a screenshot or a raycast sweep
(the bench's own probes). Origin-shift, garbage-collection and unload-unused frames stay in: they are the real cost.
"""

import json
import math
import re
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402
import pandas as pd  # noqa: E402

ROOT = Path(__file__).resolve().parents[3]
SPIKE = ROOT / "artifacts" / "world-streaming-spike"
RUNS = SPIKE / "runs"
OUT = SPIKE / "analysis"

PHASE_RUNNING = 2
FLAG_SHOT, FLAG_SHIFT, FLAG_RAYCAST, FLAG_UNLOAD_UNUSED, FLAG_GC = 1, 2, 4, 8, 16
PROBES = FLAG_SHOT | FLAG_RAYCAST
HITCH_MS = 33.3
BIG_HITCH_MS = 50.0

# Chart roles (dataviz reference palette, light mode): one fixed slot per configuration, never by rank.
SURFACE, INK, INK_2, MUTED, GRID, AXIS = "#fcfcfb", "#0b0b0b", "#52514e", "#898781", "#e1e0d9", "#c3c2b7"
SLOTS = {"A ring 4 km": "#2a78d6", "A all resident": "#eb6834", "B Cesium": "#1baf7a", "no tiles (control)": MUTED}


def label(summary):
	if summary["approach"] == "B":
		return "B Cesium"
	if summary["ring_km"] < 0:
		return "no tiles (control)"
	name = "A all resident" if summary["ring_km"] >= 30 else f"A ring {summary['ring_km']:g} km"
	return name + (" (main-thread colliders)" if summary.get("colliders") == "MainThread" else "")


def load(folder):
	summary = json.loads((folder / "summary.json").read_text())
	run = {"id": folder.name, "folder": folder, "summary": summary, "label": label(summary),
		"frames": pd.read_csv(folder / "frames.csv"), "memory": pd.read_csv(folder / "memory.csv"),
		"events": pd.read_csv(folder / "events.csv", keep_default_na=False)}
	run["rss"] = pd.read_csv(folder / "rss.csv") if (folder / "rss.csv").exists() else None
	notes = folder / "machine.txt"
	if not notes.exists():
		# Editor runs go through unity.sh, which keeps its lock and load notes beside the Unity log.
		notes = folder.parent.parent / "logs" / f"{folder.name}.machine.txt"
	run["machine"] = notes.read_text() if notes.exists() else ""
	return run


def moving(frames):
	f = frames[frames.phase == PHASE_RUNNING]
	return f[(f["flags"] & PROBES) == 0]


def percentile(values, q):
	return float(np.percentile(values, q)) if len(values) else float("nan")


def minute_phase(m, threshold=30.0):
	"""The second within each minute (of the process clock) where this run's long frames cluster, or None.

	Every run showed one stall a minute at a fixed wall-clock second, in both approaches and with no streaming at all,
	so long frames are split into those at that phase (the machine's) and the rest."""
	long = m[m.dt_ms > threshold]
	if len(long) < 3:
		return None
	phases = (long.time_s.to_numpy() % 60.0)
	counts, edges = np.histogram(phases, bins=60, range=(0, 60))
	best = int(np.argmax(counts))
	if counts[best] + counts[(best + 1) % 60] + counts[best - 1] < 3:
		return None
	near = phases[np.abs(((phases - (edges[best] + 0.5)) + 30.0) % 60.0 - 30.0) <= 1.5]
	return float(np.median(near))


def periodic(time_s, phase):
	return phase is not None and abs(((time_s - phase) + 30.0) % 60.0 - 30.0) <= 1.0


def window_events(run, frame_index, before=2):
	"""Streaming events in the frames that a long frame's time covers (Unity reports a frame's time in the next frame)."""
	e = run["events"]
	near = e[(e.frame >= frame_index - before) & (e.frame <= frame_index)]
	kinds = near[~near.event.isin(["raycast", "first_frame"])]
	parts = [f"{r.event} {r.tile}".strip() for r in kinds.itertuples()]
	f = run["frames"]
	created = int(f[(f.frame >= frame_index - before) & (f.frame <= frame_index)].created.sum())
	if created:
		parts.append(f"{created} Cesium tile objects created")
	flags = int(np.bitwise_or.reduce(f[(f.frame >= frame_index - before) & (f.frame <= frame_index)]["flags"].to_numpy()))
	if flags & FLAG_SHIFT:
		parts.append("origin shift")
	if flags & FLAG_GC:
		parts.append("GC")
	if flags & FLAG_UNLOAD_UNUSED:
		parts.append("UnloadUnusedAssets running")
	return "; ".join(parts) if parts else "none recorded"


def machine_load(text):
	loads = re.findall(r"load averages: ([\d.]+) ([\d.]+) ([\d.]+)", text)
	swap = re.findall(r"used = ([\d.]+)M", text)
	before = loads[0][0] if loads else "?"
	after = loads[1][0] if len(loads) > 1 else "?"
	return f"{before} / {after}", (f"{float(swap[0]) / 1024:.1f} GB" if swap else "?")


STREAM_EVENTS = ["load_done", "unload", "colliders_job", "colliders_main", "unload_unused_start", "unload_unused_done"]


def streaming_split(run, after=2):
	"""Moving-phase frames within `after` frames of a streaming event (A: tile arrivals, collider enables, unloads,
	UnloadUnusedAssets; B: Cesium tile objects created), and the rest."""
	f, e = run["frames"], run["events"]
	m = moving(f)
	if run["summary"]["approach"] == "A":
		marks = e[e.event.isin(STREAM_EVENTS)].frame.to_numpy()
	else:
		marks = f[f.created > 0].frame.to_numpy()
	near = np.zeros(len(m), dtype=bool)
	frames = m.frame.to_numpy()
	for k in range(after + 1):
		near |= np.isin(frames, marks + k)
	return m[near], m[~near]


def event_costs(run):
	"""Approach A's per-event costs from events.csv: wall time of an async load, main-thread placement, collider bake
	(wall, off the main thread) and enable, destroy on unload, and UnloadUnusedAssets."""
	e = run["events"]
	def values(name):
		return e[e.event == name].value_ms.to_numpy()
	def detail(name, key):
		rows = e[e.event == name].detail
		return np.array([float(part.split("=")[1]) for d in rows for part in d.split(" ") if part.startswith(key + "=")])
	out = {}
	for label, data in (("load_wall", values("load_done")), ("place", detail("load_done", "place_ms")), ("bake_wall", detail("colliders_job", "bake_wall_ms")),
		("enable_job", values("colliders_job")), ("enable_main", values("colliders_main")), ("destroy", values("unload")), ("unload_unused", values("unload_unused_done"))):
		out[label + "_n"] = int(len(data))
		out[label + "_p50"] = percentile(data, 50)
		out[label + "_max"] = float(data.max()) if len(data) else float("nan")
	return out


def incomplete(run):
	"""How long the view was not fully loaded while moving: B, Cesium's load progress under 100%; A, tiles in flight
	inside the ring. Returns (share of moving time, longest continuous stretch in seconds)."""
	f = run["frames"]
	m = f[f.phase == PHASE_RUNNING]
	if not len(m):
		return float("nan"), float("nan")
	flag = (m.progress < 99.99) if run["summary"]["approach"] == "B" else (m.loading > 0)
	t = m.time_s.to_numpy()
	dt = np.diff(np.concatenate([t[:1], t]))
	flag = flag.to_numpy()
	share = float(dt[flag].sum() / max(dt.sum(), 1e-9))
	longest, current, start = 0.0, 0.0, None
	for i, value in enumerate(flag):
		if value:
			start = t[i] if start is None else start
			current = t[i] - start + dt[i]
			longest = max(longest, current)
		else:
			start = None
	return share, longest


def settled(memory, after=10.0):
	"""Memory samples from the moving phase, without the first seconds after start-up (the scene load and shader
	warm-up leave a transient of a few hundred MB that is the same with no tiles at all)."""
	moving_memory = memory[memory.phase == PHASE_RUNNING]
	if not len(moving_memory):
		return memory
	start = memory.time_s.iloc[0]
	later = moving_memory[moving_memory.time_s >= start + after]
	return later if len(later) else moving_memory


def row(run):
	s, f = run["summary"], run["frames"]
	m = moving(f)
	dt = m.dt_ms.to_numpy()
	worst = m.loc[m.dt_ms.idxmax()] if len(m) else None
	memory = run["memory"]
	running_memory = memory[memory.phase == PHASE_RUNNING] if (memory.phase == PHASE_RUNNING).any() else memory
	shift = m[(m["flags"] & FLAG_SHIFT) != 0]
	quiet = m[(m["flags"] & FLAG_SHIFT) == 0]
	load_text = machine_load(run["machine"])
	r = {
		"run": run["id"], "config": run["label"], "editor": bool(s["editor"]), "speed": s["speed_multiplier"],
		"legs": ",".join(s["legs"]), "error": s["error"],
		"frames": int(len(m)), "median_ms": percentile(dt, 50), "p95_ms": percentile(dt, 95), "p99_ms": percentile(dt, 99),
		"max_ms": float(dt.max()) if len(dt) else float("nan"),
		"hitches_33": int((dt > HITCH_MS).sum()), "hitches_50": int((dt > BIG_HITCH_MS).sum()),
		"cpu_main_median_ms": percentile(m.cpu_main_ms.to_numpy(), 50), "gpu_median_ms": percentile(m.gpu_ms.to_numpy(), 50),
		"worst_frame": int(worst.frame) if worst is not None else -1,
		"worst_distance_m": float(worst.distance_m) if worst is not None else float("nan"),
		"worst_events": window_events(run, int(worst.frame)) if worst is not None else "",
		"triangles_median": percentile(m.triangles.to_numpy(), 50), "triangles_max": float(m.triangles.max()) if len(m) else float("nan"),
		"batches_median": percentile(m.batches.to_numpy(), 50),
		"shift_frames": int(len(shift)), "shift_median_ms": percentile(shift.dt_ms.to_numpy(), 50), "shift_p95_ms": percentile(shift.dt_ms.to_numpy(), 95),
		"quiet_median_ms": percentile(quiet.dt_ms.to_numpy(), 50), "quiet_p95_ms": percentile(quiet.dt_ms.to_numpy(), 95),
		"system_used_peak_mb": memory.system_used_b.max() / 1e6, "system_used_median_mb": running_memory.system_used_b.median() / 1e6,
		"system_used_mean_mb": running_memory.system_used_b.mean() / 1e6,
		"system_used_settled_max_mb": settled(memory).system_used_b.max() / 1e6,
		"system_used_end_mb": memory.system_used_b.iloc[-1] / 1e6,
		"native_peak_mb": memory.native_allocated_b.max() / 1e6, "managed_peak_mb": memory.mono_used_b.max() / 1e6,
		"gfx_driver_peak_mb": memory.gfx_driver_b.max() / 1e6, "gfx_used_peak_mb": memory.gfx_used_b.max() / 1e6,
		"mesh_peak_mb": memory.mesh_memory_b.max() / 1e6, "mesh_count_peak": int(memory.mesh_count.max()),
		"rss_peak_mb": (run["rss"].rss_kb.max() / 1024) if run["rss"] is not None and len(run["rss"]) else float("nan"),
		"launch_to_first_frame_s": s.get("launch_to_first_frame_s"), "first_frame_s": s.get("first_frame_s"),
		"ring_after_first_frame_s": s.get("first_full_ring_after_first_frame_s"), "first_full_ring_s": s.get("first_full_ring_s"),
		"loads": s.get("a_total_loads", ""), "unloads": s.get("a_total_unloads", ""), "cesium_tiles_created": s.get("b_tiles_created", ""),
		"load_avg_before_after": load_text[0], "swap_used_before": load_text[1],
	}
	share, longest = incomplete(run)
	r.update({"incomplete_share": share * 100.0, "incomplete_longest_s": longest})
	phase = minute_phase(m)
	long = m[m.dt_ms > HITCH_MS]
	minute = int(sum(periodic(t, phase) for t in long.time_s))
	r.update({"minute_phase_s": phase if phase is not None else float("nan"), "long_at_minute": minute, "long_other": int(len(long)) - minute})
	near, rest = streaming_split(run)
	r.update({"stream_frames": int(len(near)), "stream_p50": percentile(near.dt_ms.to_numpy(), 50), "stream_p99": percentile(near.dt_ms.to_numpy(), 99),
		"stream_max": float(near.dt_ms.max()) if len(near) else float("nan"),
		"rest_frames": int(len(rest)), "rest_p50": percentile(rest.dt_ms.to_numpy(), 50), "rest_p99": percentile(rest.dt_ms.to_numpy(), 99),
		"rest_max": float(rest.dt_ms.max()) if len(rest) else float("nan")})
	if s["approach"] == "A":
		r.update(event_costs(run))
	return r


def fmt(value, digits=1):
	if value is None or value == "" or (isinstance(value, float) and math.isnan(value)):
		return "–"
	if isinstance(value, (int, np.integer)):
		return f"{value:,}"
	if isinstance(value, float):
		return f"{value:,.{digits}f}"
	return str(value)


def table(rows, columns):
	head = "| " + " | ".join(c[0] for c in columns) + " |"
	rule = "|" + "|".join("---" for _ in columns) + "|"
	body = ["| " + " | ".join(fmt(r[c[1]], c[2] if len(c) > 2 else 1) for c in columns) + " |" for r in rows]
	return "\n".join([head, rule] + body)


def style(ax):
	ax.set_facecolor(SURFACE)
	for spine in ("top", "right"):
		ax.spines[spine].set_visible(False)
	for spine in ("left", "bottom"):
		ax.spines[spine].set_color(AXIS)
	ax.tick_params(colors=MUTED, labelsize=8)
	ax.yaxis.label.set_color(INK_2)
	ax.xaxis.label.set_color(INK_2)
	ax.grid(True, color=GRID, linewidth=0.6)
	ax.set_axisbelow(True)


def figure_timeline(run, path):
	frames = run["frames"]
	m = frames[frames.phase == PHASE_RUNNING]
	clean = m[(m["flags"] & PROBES) == 0]
	probes = m[(m["flags"] & PROBES) != 0]
	colour = SLOTS.get(run["label"], "#2a78d6")
	fig, (top, bottom) = plt.subplots(2, 1, figsize=(10, 4.6), sharex=True, gridspec_kw={"height_ratios": [3, 1]}, facecolor=SURFACE)
	top.plot(clean.distance_m / 1000.0, clean.dt_ms, color=colour, linewidth=0.6)
	ceiling = max(40.0, float(np.percentile(clean.dt_ms, 99.9)) * 1.3) if len(clean) else 40.0
	top.set_ylim(0, ceiling)
	probe_km = probes.drop_duplicates("distance_m").distance_m / 1000.0
	top.scatter(probe_km, np.full(len(probe_km), ceiling * 0.03), marker="|", s=60, color=MUTED, zorder=3, label="screenshot and raycast probes (left out)")
	for r in clean[clean.dt_ms > ceiling].itertuples():
		top.annotate(f"{r.dt_ms:.0f}", (r.distance_m / 1000.0, ceiling), fontsize=7, color=INK_2, ha="center", va="bottom", annotation_clip=False)
	top.set_ylabel("frame time, ms")
	top.set_title(f"{run['id']}: {run['label']}, x{run['summary']['speed_multiplier']:g} service speed (labels: frames above the axis, ms)",
		fontsize=9, color=INK, loc="left", pad=14)
	top.legend(fontsize=7, frameon=False, loc="upper right", labelcolor=INK_2)
	activity = m.completed + m.created + m.unloaded
	busy = m[activity > 0]
	bottom.bar(busy.distance_m / 1000.0, activity[activity > 0], width=0.03, color=colour)
	bottom.set_ylabel("tile events\nper frame", fontsize=8)
	bottom.set_xlabel("distance along the path, km")
	for ax in (top, bottom):
		style(ax)
	fig.tight_layout()
	fig.savefig(path, dpi=130, facecolor=SURFACE)
	plt.close(fig)


def figure_cdf(runs, path, title):
	fig, ax = plt.subplots(figsize=(7, 4), facecolor=SURFACE)
	for index, run in enumerate(runs):
		dt = np.sort(moving(run["frames"]).dt_ms.to_numpy())
		if not len(dt):
			continue
		y = np.arange(1, len(dt) + 1) / len(dt)
		colour = SLOTS.get(run["label"], "#2a78d6")
		ax.plot(dt, y, color=colour, linewidth=2, label=run["label"])
	for index, run in enumerate(runs):
		dt = np.sort(moving(run["frames"]).dt_ms.to_numpy())
		if len(dt):
			q = 0.15 + 0.2 * index
			ax.annotate(run["label"], (dt[int(q * len(dt))], q), fontsize=8, color=INK_2, xytext=(8, 0), textcoords="offset points", va="center")
	ax.set_xscale("log")
	ax.set_xlabel("frame time, ms (log scale)")
	ax.set_ylabel("share of frames at or below")
	ax.set_title(title, fontsize=10, color=INK, loc="left")
	ax.legend(fontsize=8, frameon=False, labelcolor=INK_2, loc="lower right")
	style(ax)
	fig.tight_layout()
	fig.savefig(path, dpi=130, facecolor=SURFACE)
	plt.close(fig)


def figure_memory(runs, path, title):
	fig, ax = plt.subplots(figsize=(7, 4), facecolor=SURFACE)
	for run in runs:
		mem = run["memory"]
		if not len(mem):
			continue
		t = mem.time_s - mem.time_s.iloc[0]
		colour = SLOTS.get(run["label"], "#2a78d6")
		ax.plot(t, mem.system_used_b / 1e6, color=colour, linewidth=2, label=run["label"])
	ax.set_xlabel("time since the bench started, s")
	ax.set_ylabel("process memory (System Used Memory), MB")
	ax.set_title(title, fontsize=10, color=INK, loc="left")
	ax.legend(fontsize=8, frameon=False, labelcolor=INK_2, loc="lower right")
	style(ax)
	fig.tight_layout()
	fig.savefig(path, dpi=130, facecolor=SURFACE)
	plt.close(fig)


def aggregate(runs):
	"""Pools runs of one configuration, place (player or editor) and speed; probe frames stay out."""
	groups = {}
	for run in runs:
		if run["summary"].get("error"):
			continue
		key = (run["label"], "editor" if run["summary"]["editor"] else "player", run["summary"]["speed_multiplier"], "quiet" in run["id"])
		groups.setdefault(key, []).append(run)
	rows = []
	for (config, kind, speed, quiet), members in sorted(groups.items()):
		dt = np.concatenate([moving(r["frames"]).dt_ms.to_numpy() for r in members])
		rings = [r["summary"].get("first_full_ring_after_first_frame_s") for r in members if r["summary"].get("first_full_ring_after_first_frame_s") is not None]
		rows.append({"config": config, "kind": kind + (" (quiet)" if quiet else ""), "speed": speed, "runs": len(members), "frames": int(len(dt)),
			"median_ms": percentile(dt, 50), "p95_ms": percentile(dt, 95), "p99_ms": percentile(dt, 99), "max_ms": float(dt.max()) if len(dt) else float("nan"),
			"hitches_33": int((dt > HITCH_MS).sum()), "hitches_50": int((dt > BIG_HITCH_MS).sum()),
			"system_used_peak_mb": max(settled(r["memory"]).system_used_b.max() for r in members) / 1e6,
			"ring_s": float(np.median(rings)) if rings else float("nan")})
	return rows


def montage(runs, shot, path):
	"""The same named point from several runs, the horizon band of each stacked full width, labelled by configuration."""
	images = []
	for run in runs:
		file = run["folder"] / "shots" / f"{run['id']}-{shot}.png"
		if file.exists():
			image = plt.imread(file)
			band = image[int(image.shape[0] * 0.30):int(image.shape[0] * 0.62)]
			images.append((run, band))
	if not images:
		return False
	height = images[0][1].shape[0] / images[0][1].shape[1] * 12.0
	fig, axes = plt.subplots(len(images), 1, figsize=(12.0, (height + 0.35) * len(images)), facecolor=SURFACE)
	axes = np.atleast_1d(axes)
	for ax, (run, image) in zip(axes, images):
		ax.imshow(image)
		ax.set_axis_off()
		ax.set_title(f"{run['label']} ({run['id']}, {shot})", fontsize=9, color=INK, loc="left")
	fig.tight_layout()
	fig.savefig(path, dpi=110, facecolor=SURFACE)
	plt.close(fig)
	return True


def hitch_rows(run, limit=8):
	m = moving(run["frames"])
	phase = minute_phase(m)
	worst = m[m.dt_ms > HITCH_MS].sort_values("dt_ms", ascending=False).head(limit)
	return [{"run": run["id"], "config": run["label"], "frame": int(r.frame), "dt_ms": float(r.dt_ms), "distance_m": float(r.distance_m),
		"minute": "yes" if periodic(r.time_s, phase) else "no", "cpu_main_ms": float(r.cpu_main_ms),
		"events": window_events(run, int(r.frame))} for r in worst.itertuples()]


def main():
	OUT.mkdir(parents=True, exist_ok=True)
	runs = [load(folder) for folder in sorted(RUNS.iterdir()) if (folder / "summary.json").exists() and (folder / "frames.csv").exists()]
	rows = [row(run) for run in runs]
	pd.DataFrame(rows).to_csv(OUT / "runs.csv", index=False)
	hitches = [h for run in runs for h in hitch_rows(run)]
	pd.DataFrame(hitches).to_csv(OUT / "hitches.csv", index=False)

	frame_columns = [("run", "run"), ("configuration", "config"), ("speed", "speed", 0), ("legs", "legs"), ("frames", "frames"),
		("median ms", "median_ms", 2), ("p95 ms", "p95_ms", 2), ("p99 ms", "p99_ms", 2), ("worst ms", "max_ms", 1),
		("> 33 ms", "hitches_33"), ("> 50 ms", "hitches_50"), ("CPU main median ms", "cpu_main_median_ms", 2), ("GPU median ms", "gpu_median_ms", 2),
		("triangles median", "triangles_median", 0), ("batches median", "batches_median", 0)]
	memory_columns = [("run", "run"), ("configuration", "config"), ("System Used mean while moving MB", "system_used_mean_mb", 0),
		("max after start-up MB", "system_used_settled_max_mb", 0), ("at the end MB", "system_used_end_mb", 0), ("overall peak MB", "system_used_peak_mb", 0),
		("RSS peak MB (ps)", "rss_peak_mb", 0), ("native allocated peak MB", "native_peak_mb", 0),
		("managed peak MB", "managed_peak_mb", 1), ("GPU driver peak MB", "gfx_driver_peak_mb", 0), ("Gfx Used peak MB", "gfx_used_peak_mb", 0),
		("mesh memory peak MB", "mesh_peak_mb", 0), ("meshes peak", "mesh_count_peak")]
	loading_columns = [("run", "run"), ("configuration", "config"), ("launch to first frame s", "launch_to_first_frame_s", 2),
		("engine to first frame s", "first_frame_s", 2), ("first frame to full ring s", "ring_after_first_frame_s", 2),
		("A loads", "loads"), ("A unloads", "unloads"), ("B tile objects created", "cesium_tiles_created"),
		("moving time not fully loaded %", "incomplete_share", 1), ("longest stretch s", "incomplete_longest_s", 1)]
	shift_columns = [("run", "run"), ("configuration", "config"), ("origin-shift frames", "shift_frames"), ("shift median ms", "shift_median_ms", 2),
		("shift p95 ms", "shift_p95_ms", 2), ("other median ms", "quiet_median_ms", 2), ("other p95 ms", "quiet_p95_ms", 2)]
	worst_columns = [("run", "run"), ("configuration", "config"), ("worst ms", "max_ms", 1), ("at m", "worst_distance_m", 0), ("tile events around it", "worst_events"),
		("frames over 33 ms", "hitches_33"), ("of them at the once-a-minute stall", "long_at_minute"), ("other", "long_other")]
	split_columns = [("run", "run"), ("configuration", "config"), ("frames near a streaming event", "stream_frames"), ("their p50 ms", "stream_p50", 2),
		("their p99 ms", "stream_p99", 2), ("their worst ms", "stream_max", 1), ("other frames", "rest_frames"), ("other p50 ms", "rest_p50", 2),
		("other p99 ms", "rest_p99", 2), ("other worst ms", "rest_max", 1)]
	event_columns = [("run", "run"), ("loads", "load_wall_n"), ("load wall p50 ms", "load_wall_p50", 0), ("load wall max ms", "load_wall_max", 0),
		("place max ms", "place_max", 2), ("bake wall p50 ms", "bake_wall_p50", 0), ("bake wall max ms", "bake_wall_max", 0),
		("enable max ms (job-baked)", "enable_job_max", 2), ("enable max ms (main thread)", "enable_main_max", 1), ("destroy max ms", "destroy_max", 2),
		("UnloadUnusedAssets p50 ms", "unload_unused_p50", 1), ("UnloadUnusedAssets max ms", "unload_unused_max", 1)]
	machine_columns = [("run", "run"), ("load average before / after", "load_avg_before_after"), ("swap used before", "swap_used_before"), ("error", "error")]

	sections = ["# Spike analysis tables", "", "Generated by tools/spikes/world-streaming/analyze.py from artifacts/world-streaming-spike/runs/.", ""]
	a_rows = [r for r in rows if r["config"].startswith("A")]
	for title, columns in (("Frame time (moving phase, probe frames excluded)", frame_columns),
		("Frames near streaming events against the rest (an event's frame and the two after it)", split_columns), ("Memory", memory_columns),
		("Cold start and first ring", loading_columns), ("Origin shifts", shift_columns), ("Worst hitch per run", worst_columns), ("Machine load", machine_columns)):
		sections += [f"## {title}", "", table(rows, columns), ""]
	if a_rows:
		sections += ["## Approach A's streaming events", "", "Wall times are asynchronous (they span frames); placement, enabling and destroying are main-thread costs.", "", table(a_rows, event_columns), ""]
	if hitches:
		hitch_columns = [("run", "run"), ("configuration", "config"), ("frame", "frame"), ("ms", "dt_ms", 1), ("main thread ms", "cpu_main_ms", 1),
			("at m", "distance_m", 0), ("at the minute stall", "minute"), ("tile events around it", "events")]
		sections += ["## Longest frames per run (top 8 over 33 ms)", "", table(hitches, hitch_columns), ""]
	shots = []
	for run in runs:
		for shot in run["summary"].get("shots", []):
			r = shot.get("raycast", {})
			shots.append({"run": run["id"], "config": run["label"], "shot": shot["name"], "hits": r.get("hits"), "mean_m": r.get("mean_hit_m"),
				"nearest_m": r.get("nearest_hit_m"), "cast_ms": r.get("cast_ms"), "sync_ms": r.get("sync_transforms_ms")})
	if shots:
		shot_columns = [("run", "run"), ("configuration", "config"), ("point", "shot"), ("hits of 360", "hits"), ("mean hit m", "mean_m", 0),
			("nearest m", "nearest_m", 0), ("360 rays ms", "cast_ms", 2), ("SyncTransforms ms", "sync_ms", 2)]
		sections += ["## Raycast sweeps at the named points", "", table(shots, shot_columns), ""]
	aggregates = aggregate(runs)
	if aggregates:
		aggregate_columns = [("configuration", "config"), ("where", "kind"), ("speed", "speed", 0), ("runs", "runs"), ("frames", "frames"),
			("median ms", "median_ms", 2), ("p95 ms", "p95_ms", 2), ("p99 ms", "p99_ms", 2), ("worst ms", "max_ms", 1), ("> 33 ms", "hitches_33"),
			("> 50 ms", "hitches_50"), ("System Used max after start-up MB", "system_used_peak_mb", 0), ("first ring s (median)", "ring_s", 2)]
		sections[4:4] = ["## By configuration (runs pooled: the three 1x legs are one path)", "", table(aggregates, aggregate_columns), ""]
		pd.DataFrame(aggregates).to_csv(OUT / "configurations.csv", index=False)
	(OUT / "tables.md").write_text("\n".join(sections) + "\n", encoding="utf-8")

	for run in runs:
		figure_timeline(run, OUT / f"timeline-{run['id']}.png")
	groups = {}
	for run in runs:
		if run["summary"]["colliders"] == "MainThread":
			continue  # a collider variant: its own tables, not a series beside the configurations
		quiet = "quiet" if "quiet" in run["id"] else "shared"
		key = ("editor" if run["summary"]["editor"] else "player", run["summary"]["speed_multiplier"], ",".join(run["summary"]["legs"]), quiet)
		groups.setdefault(key, []).append(run)
	for (kind, speed, legs, quiet), members in groups.items():
		name = f"{kind}-x{speed:g}-{'all' if legs.count(',') == 2 else legs}-{quiet}"
		where = f"{kind}, x{speed:g}, {'whole path' if legs.count(',') == 2 else legs}, {'quiet machine' if quiet == 'quiet' else 'shared machine'}"
		figure_cdf(members, OUT / f"cdf-{name}.png", f"Frame time distribution ({where})")
		figure_memory(members, OUT / f"memory-{name}.png", f"Process memory over the run ({where})")
	for speed_runs in (["b-x5", "a4-x5", "aall-x5"], ["b-x5-quiet", "a4-x5-quiet", "aall-x5-quiet", "none-x5-quiet"]):
		members = [r for name in speed_runs for r in runs if r["id"] == name]
		if len(members) < 2:
			continue
		for shot in ("pier11", "east-34th", "roosevelt-island", "hell-gate", "upper-bay", "st-george"):
			montage(members, shot, OUT / f"shots-{speed_runs[0]}-{shot}.png")
	print(f"{len(runs)} runs -> {OUT}")


if __name__ == "__main__":
	main()
