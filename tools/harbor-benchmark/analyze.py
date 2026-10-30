"""Strict raw-evidence verdicts. Controls explain tails; no frame is removed from a gate."""
import argparse
import csv
import json
from pathlib import Path
import zipfile


def rows(path):
	if not path.is_file():
		return []
	with path.open(newline='', encoding='utf-8-sig') as stream:
		return list(csv.DictReader(stream))


def streaming(frames, events, stress=False):
	lookup = {int(f['frame']): f for f in frames}
	windows = []
	missing = []
	for event in events:
		if event['kind'] not in ('tile_created', 'tile_enabled', 'tile_disabled', 'tile_destroyed', 'load_progress', 'origin_shift', 'tileset_failure'):
			continue
		frame = int(event['frame'])
		if frame not in lookup or not lookup[frame].get('wall_ms'):
			missing.append(frame)
			continue
		ids = [i for i in range(frame - 2, frame + 3) if i in lookup]
		windows.append({'frame': frame, 'kind': event['kind'], 'event_ms': float(lookup[frame]['wall_ms']),
			'frames': ids, 'wall_ms': [float(lookup[i]['wall_ms']) for i in ids], 'phase': lookup[frame].get('phase')})
	# A new accepted Start closes the running gate until its first full view. The view-ready
	# observation follows the completed rendered frame, whose id is retained as related_frame.
	boundaries = []
	for event in events:
		if event['kind'] == 'start_accepted': boundaries.append((int(event['frame']), False))
		elif event['kind'] == 'view_ready': boundaries.append((int(event.get('related_frame') or int(event['frame']) - 1), True))
	boundaries.sort(key=lambda x: x[0])
	def running(frame, phase):
		if phase in ('capture','replay','finish'): return False
		if not boundaries: return phase == 'drive'
		active = False
		for at, value in boundaries:
			if at > frame: break
			active = value
		return active
	drive = [w for w in windows if running(w['frame'], w['phase'])]
	preparation = [w for w in windows if not running(w['frame'],w['phase']) and w['phase'] not in ('capture','replay','finish')]

	bad = [w for w in drive if w['event_ms'] > 20]
	return {'verdict': 'unverified' if stress else ('fail' if bad else ('unverified' if missing or not drive else 'pass')),
		'max_event_ms': max((w['event_ms'] for w in drive), default=None), 'event_count': len(drive),
		'over_20ms': len({w['frame'] for w in bad}), 'over_20_event_records':len(bad), 'event_frame_count':len({w['frame'] for w in drive}), 'violating_frames':sorted({w['frame'] for w in bad}), 'counting':'event_count counts event records; event_frame_count and over_20ms count distinct Unity frames; per-event windows remain raw', 'preparation_event_max_ms': max((w['event_ms'] for w in preparation),default=None), 'preparation_event_count': len(preparation), 'stress_over_20ms': len({w['frame'] for w in bad}) if stress else None, 'unjoined_frames': missing, 'windows': windows,
		'note': 'every event frame from first full view until the next accepted Start is gated, including berth/turn/return frames; preparation is reported separately, no stall subtraction; ±2 windows retained; complete queue/unload coverage must also be proven'}


def memory(samples, os_peak):
	values = [int(r['system_used_bytes']) for r in samples if int(r.get('system_used_bytes') or 0) > 0]
	steady = [int(r['system_used_bytes']) for r in samples if r.get('phase') == 'drive' and int(r.get('system_used_bytes') or 0) > 0]
	peak = max(values, default=0)
	verdict = 'fail' if max(peak, os_peak) >= 2000000000 else ('unverified' if not peak or not os_peak else 'pass')
	return {'verdict': verdict, 'system_used_peak_bytes': peak, 'os_peak_rss_bytes': os_peak,
		'steady_system_used_peak_bytes': max(steady, default=None), 'limit_bytes': 2000000000,
		'note': 'complete process OS peak includes launch/preparation; profiler coverage starts before the first scene'}


def recording_evidence(folder, scenario):
	from pathlib import PureWindowsPath
	if float(scenario.get('duration_s') or 0) <= 0 or int(scenario.get('replay_rows') or 0) <= 1:
		return {'verdict':'fail','reason':'a zero-step replay is not journey evidence'}
	name=PureWindowsPath(str(scenario.get('recording') or '')).name
	telemetry=folder/'runs'/name
	if not name or not telemetry.is_file():
		return {'verdict':'unverified','missing':[str(telemetry)]}
	header={}; count=0
	with telemetry.open(encoding='utf-8-sig') as stream:
		for line in stream:
			if line.startswith('# '):
				key,_,value=line[2:].strip().partition(': ');header[key]=value
			elif line.strip(): count+=1
	required=[]
	for key in ('param.inputs','traffic_events'):
		if key=='traffic_events' and not scenario.get('traffic'): continue
		value=header.get(key,'')
		if not value or PureWindowsPath(value).name!=value:
			return {'verdict':'unverified','missing':[key]}
		required.append(folder/'runs'/value)
	missing=[str(p) for p in required if not p.is_file()]
	if missing: return {'verdict':'unverified','missing':missing}
	inputs=required[0].read_text(encoding='utf-8-sig')
	expected=scenario.get('recorded_state_digest')
	digest_ok=bool(expected) and ('# state_digest: '+expected) in inputs and expected==scenario.get('replayed_state_digest')
	valid=scenario.get('replay_identical') is True and digest_ok and count-1==int(scenario['replay_rows']) and header.get('physics_hz')=='100' and header.get('sample_hz')=='100'
	return {'verdict':'pass' if valid else 'fail','telemetry_rows':count-1,'digest_matches':digest_ok,'physics_hz':header.get('physics_hz'),'sample_hz':header.get('sample_hz')}



def switch_memory_table(folder):
	"""Retain first-view/10s/collection boundaries, with independently sampled kernel RSS."""
	if not (folder/'starts.json').is_file(): return []
	identity=json.loads((folder/'identity.json').read_text(encoding='utf-8-sig'))
	zero=float(identity.get('utc_zero_unix_s') or 0)
	process=json.loads((folder/'process.json').read_text(encoding='utf-8-sig')) if (folder/'process.json').exists() else {}
	launch=process.get('launch_unix_s')
	if launch is None and process.get('launch_utc'):
		import datetime
		launch=datetime.datetime.fromisoformat(process['launch_utc'].replace('Z','+00:00')).timestamp()
	samples=[]
	for sample in rows(folder/'os-memory.csv'):
		stamp=float(sample['unix_s']) if sample.get('unix_s') else (float(launch)+float(sample['wall_s']) if launch is not None else None)
		if stamp is not None:samples.append((stamp,int(sample['rss_bytes']),int(sample.get('physical_footprint_bytes') or 0)))
	table=[]
	for start in json.loads((folder/'starts.json').read_text(encoding='utf-8-sig')):
		for at in ('before_collection','after_collection','memory_first_view','memory_after_10s'):
			if at not in start:continue
			snapshot=dict(start[at],sequence=start['sequence'],stop_id=start['stop_id'],point=at)
			target=float(snapshot['wall_s'])+zero
			nearest=min(samples,key=lambda s:abs(s[0]-target)) if samples else None
			delta=abs(nearest[0]-target) if nearest else None
			snapshot['kernel_rss_bytes']=nearest[1] if delta is not None and delta<=.25 else None
			snapshot['kernel_sample_delta_s']=delta
			snapshot['kernel_physical_footprint_bytes']=nearest[2] if delta is not None and delta<=.25 and nearest[2]>0 else None
			table.append(snapshot)
	return table



def journey_verdict(scenario):
	failures=list(scenario.get('failures') or [])
	for key in ('depth_misses','time_holds','query_saturation','grounded_steps','dropped_simulation_s','load_screen_frames'):
		if scenario.get(key,0)!=0:failures.append(key+' is nonzero')
	if scenario.get('traffic') and (scenario.get('traffic_collisions') or 0)>0:failures.append('unintended AIS traffic collision')
	if scenario.get('functional_verdict')=='fail':failures.append('native workload failed')
	required=('depth_misses','time_holds','query_saturation','grounded_steps','dropped_simulation_s','load_screen_frames','arrival_verdict','arrival_stop_id','ordered_crossings','state_finite','replay_identical','hold')
	missing=[key for key in required if scenario.get(key) is None]
	if scenario.get('traffic') and scenario.get('traffic_collisions') is None:missing.append('traffic_collisions')
	if scenario.get('state_finite') is False:failures.append('non-finite state')
	if scenario.get('arrival_verdict')=='fail':failures.append('arrival thresholds failed')
	verdict='fail' if failures else ('pass' if scenario.get('completed_journey') and not missing else 'unverified')
	return {'verdict':verdict,'failures':failures,'missing':missing,'arrival':scenario.get('arrival_verdict'),'note':'a clean partial workload is not a completed journey; St. George arrival remains crew-blocked'}



def start_readiness(choices):
	accepted=[c for c in choices if c.get('accepted')]
	bad=[c for c in accepted if not c.get('ready') or c.get('start_to_ready_s') is None or c['start_to_ready_s']>3]
	blocked=len(choices)-len(accepted)
	return {'verdict':'fail' if bad else ('unverified' if blocked or not accepted else 'pass'),
		'accepted_verdict':'fail' if bad else ('pass' if accepted else 'unverified'),
		'accepted':len(accepted),'blocked_or_refused':blocked,'failed_accepted':len(bad),
		'max_accepted_seconds':max((c.get('start_to_ready_s') or 0 for c in accepted),default=None),
		'note':'refused or crew-blocked choices never count as a full first view; per-choice reasons and memory remain in starts.json'}


def camera_evidence(folder, scenario):
	"""Read actual post-render ECEF poses; a requested transform alone proves no motion."""
	import math
	path=folder/'camera.csv'
	if not path.is_file():return {'verdict':'unverified','evidence_validity':'invalid','missing':['camera.csv'],'reason':'No actual post-render camera path was recorded; a requested route is not valid sweep evidence'}
	samples=rows(path)
	if len(samples)<3:return {'verdict':'fail','reason':'too few rendered camera samples'}
	try:
		expected=[tuple(float(r['expected_'+axis]) for axis in ('x','y','z')) for r in samples]
		actual=[tuple(float(r['actual_'+axis]) for axis in ('x','y','z')) for r in samples]
		stamps=[float(r['wall_s']) for r in samples]
		frames=[int(r['frame']) for r in samples]
		if not all(math.isfinite(v) for point in expected+actual for v in point) or not all(math.isfinite(t) for t in stamps):raise ValueError('nonfinite camera sample')
		requested=float(scenario.get('camera_expected_distance_m') or 0)
		if not math.isfinite(requested):raise ValueError('nonfinite requested distance')
	except (ValueError,KeyError,TypeError) as error:return {'verdict':'fail','reason':str(error)}
	expected_distance=sum(math.dist(a,b) for a,b in zip(expected,expected[1:]))
	observed_distance=sum(math.dist(a,b) for a,b in zip(actual,actual[1:]))
	max_error=max(math.dist(a,b) for a,b in zip(expected,actual))
	ordered=all(b>a for a,b in zip(frames,frames[1:])) and all(b>a for a,b in zip(stamps,stamps[1:]))
	valid=scenario.get('camera_route_verified') is True and requested>1000 and ordered and max_error<=.1 and abs(expected_distance-requested)<=requested*.001 and abs(observed_distance-requested)<=requested*.001
	return {'verdict':'pass' if valid else 'fail','evidence_validity':'valid' if valid else 'invalid','samples':len(samples),'requested_distance_m':requested,'expected_sample_distance_m':expected_distance,'observed_distance_m':observed_distance,'max_error_m':max_error,'ordered_frames_and_time':ordered,'note':'positions are sampled after rendering with the follow controllers disabled; every sample is checked, no stationary sweep claim'}


def analyze(folder):
	required = ['frames.csv', 'events.csv', 'memory.csv', 'scenario.json', 'process.json', 'identity.json']
	missing = [name for name in required if not (folder / name).is_file()]
	if missing:
		return {'overall': 'unverified', 'missing': missing}
	scenario = json.loads((folder / 'scenario.json').read_text(encoding='utf-8-sig'))
	process = json.loads((folder / 'process.json').read_text(encoding='utf-8-sig'))
	frames = rows(folder / 'frames.csv')
	identity = json.loads((folder / 'identity.json').read_text(encoding='utf-8-sig'))
	resolution_frames = [r for r in frames if r.get('phase') == 'drive']
	cost=frame_cost(resolution_frames)
	wrong_resolution = [int(r['frame']) for r in resolution_frames if int(r.get('width', 0)) != 1920 or int(r.get('height', 0)) != 1080]
	result = {'resolution': {'verdict': 'fail' if wrong_resolution else ('pass' if resolution_frames else 'unverified'), 'wrong_frames': wrong_resolution}, 'scenario': scenario, 'memory': memory(rows(folder / 'memory.csv'), int(process.get('peak_rss_bytes') or 0)),
		'streaming': streaming(frames, rows(folder / 'events.csv'), scenario.get('stress', False)),
		'first_view': {'verdict': 'unverified', 'seconds': scenario.get('start_to_ready_s')},
		'displays': {'verdict': 'unverified', 'note': 'requires a matched displays-off scenario and valid player CPU/GPU timing'},
		'platform': scenario.get('platform'), 'overall': 'unverified','frame_cost':cost}
	result['switch_memory_table']=switch_memory_table(folder)
	if result['switch_memory_table']:
		columns=sorted({key for row in result['switch_memory_table'] for key in row})
		with (folder/'switch-memory.csv').open('w',newline='') as stream:
			writer=csv.DictWriter(stream,fieldnames=columns);writer.writeheader();writer.writerows(result['switch_memory_table'])
	if scenario.get('platform') == 'WindowsPlayer':
		proof = folder / 'windows-memory-proof.json'
		if not proof.is_file() or json.loads(proof.read_text(encoding='utf-8-sig')).get('verdict') != 'pass' or process.get('final_peak_read') is not True:
			if result['memory']['verdict'] != 'fail': result['memory']['verdict'] = 'unverified'
			result['memory']['windows_native_proof'] = 'pending or failed'
	zero = identity.get('utc_zero_unix_s')
	if zero is not None:
		import bisect
		import datetime
		stamps = [float(r['wall_s'])+float(zero) for r in frames]
		phases = [r.get('phase') for r in frames]
		launch = process.get('launch_unix_s')
		if launch is None and process.get('launch_utc'):
			launch = datetime.datetime.fromisoformat(process['launch_utc'].replace('Z','+00:00')).timestamp()
		steady_rss = []
		for sample in rows(folder/'os-memory.csv'):
			stamp = float(sample['unix_s']) if sample.get('unix_s') else (float(launch)+float(sample['wall_s']) if launch is not None else None)
			if stamp is None or not stamps: continue
			i = max(0,min(len(stamps)-1,bisect.bisect_right(stamps,stamp)-1))
			if phases[i] == 'drive': steady_rss.append(int(sample['rss_bytes']))
		result['memory']['steady_os_sampled_rss_peak_bytes'] = max(steady_rss,default=None)
		result['memory']['steady_os_sampling_note'] = '100 ms external samples, not an exact kernel peak for the subinterval; complete-run kernel peak is gated separately'
		accepted = next((e for e in rows(folder/'events.csv') if e['kind']=='start_accepted'),None)
		if accepted and launch is not None:
			first = next((r for r in frames if int(r['frame'])==int(accepted['frame'])),None)
			result['process_launch_to_start_accepted_s'] = float(first['wall_s'])+float(zero)-float(launch) if first else None

	if scenario.get('route') not in ('starts','switches','camera-sweep') and not str(scenario.get('route','')).startswith('start-'):
		result['journey']=journey_verdict(scenario)
		if result['journey']['verdict']=='fail':result['overall']='fail'
		result['recording']=recording_evidence(folder,scenario)
		if result['recording']['verdict']=='fail': result['overall']='fail'
	if scenario.get('route')=='camera-sweep':
		result['camera']=camera_evidence(folder,scenario)
		result['evidence_validity']=result['camera'].get('evidence_validity','invalid')
		if result['camera']['verdict']=='fail':result['overall']='fail'
	if isinstance(scenario.get('starts'),list):result['first_view']=start_readiness(scenario['starts'])
	result['streaming']['tileset_failure_events']=sum(e.get('kind')=='tileset_failure' for e in rows(folder/'events.csv'))
	if result['streaming']['tileset_failure_events']:result['overall']='fail'
	ready = scenario.get('start_to_ready_s')
	if ready is not None and scenario.get('readiness_complete'):
		result['first_view']['verdict'] = 'pass' if ready <= 3 else 'fail'
	if scenario.get('streaming_instrumentation_complete') is not True and result['streaming']['verdict'] == 'pass':
		result['streaming']['verdict'] = 'unverified'
	if any(x['verdict'] == 'fail' for x in (result['memory'], result['streaming'], result['first_view'], result['resolution'])) or scenario.get('failures'):
		result['overall'] = 'fail'
	return result


def percentile(values, fraction):
	if not values: return None
	values = sorted(values)
	return values[min(len(values)-1, int((len(values)-1)*fraction))]



def frame_cost(frames):
	result={'frames':len(frames)}
	for key in ('wall_ms','cpu_main_ms','cpu_render_ms','gpu_ms','cpu_total_ms','cpu_present_wait_ms'):
		values=[float(r.get(key) or 0) for r in frames]
		available=bool(values) and (any(v>0 for v in values) or key=='cpu_present_wait_ms' and any(int(r.get('frame_timing_timestamp') or 0)>0 for r in frames))
		result[key]={'verdict':'measured' if available else 'unverified','mean':sum(values)/len(values) if available else None,
			'p50':percentile(values,.5) if available else None,'p95':percentile(values,.95) if available else None,'p99':percentile(values,.99) if available else None,'max':max(values) if available else None}
	stamps=[int(r.get('frame_timing_timestamp') or 0) for r in frames]
	result['native_timing_samples_nonzero']=sum(s>0 for s in stamps)
	result['native_timing_unique_timestamps']=len(set(stamps)-{0})
	result['mean_fps']=1000/result['wall_ms']['mean'] if result['wall_ms']['mean'] else None
	result['note']='all drive frames retained including minute stalls; FrameTimingManager latest samples may repeat and GPU latency is not mapped to Unity frame ids; chart_tick_ms is the last refresh duration, not a per-frame CPU sum'
	return result


def display_pairs(root, folders):
	groups = {}
	for folder in folders:
		scenario = json.loads((folder/'scenario.json').read_text(encoding='utf-8-sig'))
		if scenario.get('traffic') or scenario.get('render_mode') != 'both' or scenario.get('stress'): continue
		key = (str(folder.parent.relative_to(root)), scenario.get('route'), scenario.get('minute'), scenario.get('visibility_m'))
		if not (folder.name.endswith('traffic0-displays0') or folder.name.endswith('traffic0-displays1')): continue
		groups.setdefault(key, {})[bool(scenario.get('displays'))] = folder
	results = []
	for key, pair in groups.items():
		if True not in pair or False not in pair: continue
		series = {}
		for shown, folder in pair.items():
			frames = [r for r in rows(folder/'frames.csv') if r.get('phase') == 'drive']
			wall = [float(r['wall_ms']) for r in frames]
			series['on' if shown else 'off'] = {'folder': str(folder.relative_to(root)), 'frames': len(wall),
				'mean_ms': sum(wall)/len(wall) if wall else None, 'p50_ms': percentile(wall,.5), 'p95_ms': percentile(wall,.95), 'p99_ms': percentile(wall,.99),
				'chart_last_refresh_p50_ms': percentile([float(r.get('chart_tick_ms') or 0) for r in frames], .5),'frame_cost':frame_cost(frames)}
		on, off = series['on']['mean_ms'], series['off']['mean_ms']
		results.append({'group':key[0], 'route': key[1], 'series': series,
			'combined_displays_mean_difference_ms': on-off if on and off else None,
			'combined_displays_mean_frame_share': (on-off)/on if on and off else None,
			'verdict': 'unverified', 'note': 'paired player distributions, no frames removed; signed difference includes run-to-run/host noise and is not a per-display GPU attribution'})
	return results


def main():
	p = argparse.ArgumentParser()
	p.add_argument('source', type=Path)
	p.add_argument('--output', type=Path)
	a = p.parse_args()
	source = a.source.resolve()
	if source.suffix.lower() == '.zip':
		target = a.output or source.with_suffix('')
		if target.exists():
			raise SystemExit('archive extraction target already exists')
		with zipfile.ZipFile(source) as archive:
			for name in archive.namelist():
				if not (target / name).resolve().is_relative_to(target.resolve()):
					raise SystemExit('unsafe archive path: ' + name)
			archive.extractall(target)
		source = target
	folders = sorted({p.parent for p in source.rglob('scenario.json')})
	results = {str(f.relative_to(source)): analyze(f) for f in folders}
	if not results:
		results = {'.': analyze(source)}
	for folder in folders:
		(folder / 'verdict.json').write_text(json.dumps(results[str(folder.relative_to(source))], indent=2, allow_nan=False)+'\n')
	results['_display_pairs'] = display_pairs(source, folders)
	output = a.output if a.source.suffix.lower() != '.zip' else source / 'analysis.json'
	text = json.dumps(results, indent=2, allow_nan=False) + '\n'
	if output:
		output.write_text(text)
	else:
		print(text)


if __name__ == '__main__':
	main()
