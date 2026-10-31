"""Read opt-in development-player attribution without inventing GPU/frame or missing-marker evidence."""
import argparse
import csv
import json
from pathlib import Path

from analyze import frame_tail, percentile, rows


def summarize(folder):
	folder = Path(folder)
	profile = json.loads((folder / 'profile.json').read_text())
	retained = {int(row['frame']): row for row in rows(folder / 'profile-frames.csv')}
	events = rows(folder / 'events.csv')
	frames = []
	native_gpu = []
	with (folder / 'frames.csv').open(newline='') as stream:
		for row in csv.DictReader(stream):
			frames.append({key: row[key] for key in ('frame', 'phase', 'wall_ms')})
			if int(row.get('frame_timing_timestamp') or 0) and float(row.get('gpu_ms') or 0) > 0:
				native_gpu.append(float(row['gpu_ms']))
	lookup = {int(row['frame']): row for row in frames}
	unmatched = [frame for frame, row in retained.items() if frame not in lookup or
		abs(float(row['wall_ms']) - float(lookup[frame]['wall_ms'])) > 1e-6]
	gate = frame_tail(frames, events)
	unprofiled = sorted(set(gate['violating_frames']) - retained.keys())
	collector_valid = profile.get('sampling_method') == 'continuous per-frame sums'
	samples = {}
	for row in rows(folder / 'profile-markers.csv'):
		samples[(int(row['frame']), int(row['marker']))] = (int(row['value']), int(row['count']))
	setup = {int(event['frame']) for event in events if event['kind'] == 'profile_setup'}
	slow = []
	for frame, row in sorted(retained.items()):
		wall = float(row['wall_ms'])
		if wall <= 20:
			continue
		cpu = float(row['main_thread_cpu_ms'])
		markers, native_counters = {}, {}
		for marker in profile['markers']:
			target = native_counters if marker['name'] in ('CPU Main Thread Frame Time', 'CPU Render Thread Frame Time') else markers
			bound = marker.get('bound_at_frame')
			if not marker['available'] or bound is None or frame < bound:
				target[marker['name']] = None
				continue
			value, count = samples.get((frame, marker['id']), (0, 0))
			target[marker['name']] = {'value': value, 'count': count, 'unit': marker['unit'],
				'milliseconds': value * 1e-6 if marker['unit'] == 'TimeNanoseconds' else None}
		slow.append({'frame': frame, 'phase': lookup.get(frame, {}).get('phase'), 'wall_ms': wall,
			'main_thread_cpu_ms': cpu if cpu >= 0 else None,
			'main_thread_cpu_fraction': cpu / wall if cpu >= 0 and wall > 0 else None,
			'profile_setup': frame in setup, 'markers': markers, 'unjoined_native_counters': native_counters})
	drops = [event for event in events if event['kind'] == 'frame_dropped']
	slow_by_frame = {row['frame']: row for row in slow}
	drop_profiles = [{'callback_frame': int(event['frame']), 'elapsed_frame': int(event['frame']) - 1,
		'dropped_seconds': float(event['value']) if event.get('value') else None,
		'profile': slow_by_frame.get(int(event['frame']) - 1)} for event in drops]
	return {'schema': 'ferrysim-frame-attribution/1', 'source': str(folder.resolve()),
		'coverage': 'incomplete' if not collector_valid or profile['overflow'] or unmatched or unprofiled or gate['invalid_frames'] or any(row['profile'] is None for row in drop_profiles) else 'complete retained tail',
		'collector_valid': collector_valid,
		'development_build': profile['development_build'], 'profile_overflow': profile['overflow'],
		'unmatched_profile_frames': unmatched, 'unprofiled_slow_frames': unprofiled,
		'frame_tail': gate, 'drop_callbacks': drops, 'drop_frame_profiles': drop_profiles,
		'slow_frames': slow, 'unavailable_markers': [m['name'] for m in profile['markers'] if not m['available']],
		'gpu_timing_note': 'FrameTimingManager GPU samples have independent native timestamps; they are not joined to Unity frame IDs.',
		'native_cpu_counter_note': 'CPU Main/Render Thread Frame Time counters are latest native timing samples, not the continuously sampled Main Thread scope; they are not attributed to the Unity frame.',
		'gpu_distribution_ms': {'samples': len(native_gpu), 'p95': percentile(native_gpu, .95),
			'p99': percentile(native_gpu, .99), 'max': max(native_gpu, default=None)},
		'limits': 'Development profiling adds overhead. Marker durations overlap and are never summed. A low thread CPU fraction shows waiting or descheduling, not its unique cause. Missing markers cannot exonerate shader compilation, I/O, GC or native work.'}


def main():
	parser = argparse.ArgumentParser()
	parser.add_argument('folder', type=Path)
	parser.add_argument('--output', type=Path, required=True)
	args = parser.parse_args()
	args.output.write_text(json.dumps(summarize(args.folder), indent=2) + '\n')


if __name__ == '__main__':
	main()
