"""The same installed-player matrix on Mac and Windows. No scenario silently substitutes for a long route."""
import json
from pathlib import Path


def matrix():
	rows = []
	for route in ('st-george', 'astoria', 'soundview', 'astoria-return'):
		for traffic, displays in ((False, True), (True, True), (False, False)):
			rows.append(dict(id=f'{route}-traffic{int(traffic)}-displays{int(displays)}', route=route, traffic=traffic,
				displays=displays, render='both', duration=6000, capture=False, minute=-1, visibility=0))
	# Controls and selected rendering conditions repeat at least three times across the minute stall.
	for repeat in range(3):
		for render in ('both', 'terrain', 'none'):
			rows.append(dict(id=f'control-{render}-{repeat+1}', route='astoria', traffic=False, displays=True,
				render=render, duration=90, capture=False, minute=-1, visibility=0))
		for name, minute, visibility in (('night', 180, 0), ('fog', 738, 463), ('busy', 1015, 0)):
			rows.append(dict(id=f'{name}-{repeat+1}', route='astoria-busy' if name=='busy' else 'astoria', traffic=True, displays=True, render='both',
				duration=90, capture=False, minute=minute, visibility=visibility))
		rows.append(dict(id=f'starts-{repeat+1}', route='starts', traffic=False, displays=True, render='both', duration=0,
			capture=True, minute=-1, visibility=0))
	for repeat in range(3):
		for stop in ('87','137','89','112'):
			rows.append(dict(id=f'cold-start-{stop}-{repeat+1}', route='start-'+stop, traffic=False, displays=True, render='both', duration=0, capture=True, minute=-1, visibility=0))
	rows.append(dict(id='warm-switches', route='switches', traffic=False, displays=True, render='both', duration=0, capture=True, minute=-1, visibility=0))
	for cycles, collect in ((1,False),(2,False),(2,True)):
		rows.append(dict(id=f'switch-memory-{cycles*12}'+('-collect' if collect else ''), route='switches', traffic=False, displays=True, render='both', duration=0, capture=False, minute=-1, visibility=0, switch_diagnostics=True, switch_cycles=cycles, switch_collect=collect))
	rows.append(dict(id='integrated-stress-5x', route='soundview', traffic=True, displays=True, render='both', duration=6000, capture=False, minute=-1, visibility=0, rate=5))
	rows.append(dict(id='camera-stress-5x', route='camera-sweep', traffic=False, displays=True, render='both', duration=0,
		capture=False, minute=-1, visibility=0))
	for route in ('st-george', 'astoria', 'soundview'):
		rows.append(dict(id=route+'-captures', route=route, traffic=True, displays=True, render='both', duration=6000,
			capture=True, minute=-1, visibility=0))
	return {'schema': 'ferrysim-harbor-benchmark-matrix/1', 'resolution': [1920,1080], 'process_cache': 'cold process, OS cache uncontrolled',
		'quality': 'shipping build defaults; vSync=0, targetFrameRate=-1, SSE=16, terrain=192 MiB, skyline=64 MiB', 'scenarios': rows}


if __name__ == '__main__':
	Path(__file__).with_name('matrix.json').write_text(json.dumps(matrix(), indent=2)+'\n')
