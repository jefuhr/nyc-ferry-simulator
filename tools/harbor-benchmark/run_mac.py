"""Run under the shared heavy+Unity locks; each bounded process releases them before the next scenario."""
import argparse
import csv
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import tempfile
import time
from matrix import matrix
from analyze import analyze
from mac_memory import MacMemory


def run(player, definitions, out, scenario, timeout, timing=False):
	out.mkdir(parents=True, exist_ok=False)
	args = [str(player), '-ferrysim-route', scenario['route'], '-ferrysim-route-file', str(definitions),
		'-ferrysim-benchmark-output', str(out), '-ferrysim-traffic', str(int(scenario['traffic'])),
		'-ferrysim-displays', str(int(scenario['displays'])), '-ferrysim-render', scenario['render'],
		'-ferrysim-duration', str(scenario['duration']), '-ferrysim-minute', str(scenario['minute']),
		'-ferrysim-visibility', str(scenario['visibility']), '-ferrysim-capture', str(int(scenario['capture'])),
		'-ferrysim-timing', str(int(timing)), '-ferrysim-rate', str(scenario.get('rate', 1)), '-screen-width', '1920', '-screen-height', '1080', '-screen-fullscreen', '0', '-logFile', str(out / 'Player.log')]
	args += ['-ferrysim-switch-diagnostics',str(int(scenario.get('switch_diagnostics',False))),'-ferrysim-switch-cycles',str(scenario.get('switch_cycles',1)),'-ferrysim-switch-collect',str(int(scenario.get('switch_collect',False)))]
	(out / 'command.json').write_text(json.dumps(args, indent=2)+'\n')
	launch = time.time()
	load_at_launch = os.getloadavg()
	peak_sampled = 0
	peak_footprint=0
	native_memory=MacMemory()
	with tempfile.TemporaryDirectory(prefix='ferrysim-harbor-empty-') as empty:
		with (out / 'stdout.log').open('wb') as log, (out / 'os-memory.csv').open('w', newline='') as memory:
			writer = csv.writer(memory);writer.writerow(['unix_s','wall_s','rss_bytes','cpu_percent','native_resident_bytes','physical_footprint_bytes','peak_physical_footprint_bytes'])
			process = subprocess.Popen(args, cwd=empty, stdout=log, stderr=subprocess.STDOUT,
				env={'HOME': os.environ['HOME'], 'USER': os.environ.get('USER',''), 'PATH':'/usr/bin:/bin', 'TMPDIR':os.environ.get('TMPDIR','/tmp')})
			usage = None
			while usage is None:
				pid, status, usage = os.wait4(process.pid, os.WNOHANG)
				if pid:
					process.returncode = os.waitstatus_to_exitcode(status)
					break
				usage = None
				if time.time() - launch > timeout:
					process.terminate()
					deadline=time.time()+5
					while time.time()<deadline:
						pid,status,usage=os.wait4(process.pid,os.WNOHANG)
						if pid: break
						time.sleep(.1)
					if not pid:
						process.kill()
						_,status,usage=os.wait4(process.pid,0)
					process.returncode = os.waitstatus_to_exitcode(status)
					break
				try:
					data = subprocess.check_output(['/bin/ps', '-o', 'rss=,%cpu=', '-p', str(process.pid)], text=True).split()
					if len(data) >= 2:
						rss = int(data[0])*1024;peak_sampled=max(peak_sampled,rss)
						native=native_memory.read(process.pid);peak_footprint=max(peak_footprint,native['peak_physical_footprint_bytes'])
						writer.writerow([time.time(),time.time()-launch,rss,float(data[1]),native['resident_bytes'],native['physical_footprint_bytes'],native['peak_physical_footprint_bytes']])
				except (subprocess.CalledProcessError, ValueError, OSError):
					pass
				time.sleep(.1)
	result = {'exit_code':process.returncode,'launch_unix_s':launch,'complete_wall_s':time.time()-launch,
		'peak_rss_bytes':int(usage.ru_maxrss),'sampled_peak_rss_bytes':peak_sampled, 'kernel_peak_physical_footprint_bytes':peak_footprint,'physical_footprint_source':'proc_pid_rusage RUSAGE_INFO_V4 lifetime_max_phys_footprint; sampled through process life', 'peak_source':'wait4 ru_maxrss (bytes on Darwin); complete child lifetime',
		'system':platform.platform(),'load_average_at_launch':load_at_launch,'load_average_at_end':os.getloadavg(),'process_cache':'cold process, OS cache uncontrolled'}
	(out / 'process.json').write_text(json.dumps(result,indent=2)+'\n')
	(out / 'verdict.json').write_text(json.dumps(analyze(out),indent=2)+'\n')
	return result


def main():
	p=argparse.ArgumentParser()
	p.add_argument('--player',type=Path,required=True)
	p.add_argument('--routes',type=Path,required=True)
	p.add_argument('--out',type=Path,required=True)
	p.add_argument('--scenario',action='append',help='exact matrix id; omitted runs full matrix')
	p.add_argument('--timeout',type=float,default=20000)
	p.add_argument('--timing',action='store_true',help='diagnostic FrameTimingStats build only; never a shipping verdict')
	p.add_argument('--already-locked',action='store_true')
	p.add_argument('--quick',action='store_true',help='60 simulated seconds, explicitly incomplete journey, for instrumentation verification')
	a=p.parse_args()
	player=a.player.resolve();definitions=a.routes.resolve();root=a.out.resolve()
	root.mkdir(parents=True,exist_ok=True)
	(root/'matrix.json').write_text(json.dumps(matrix(),indent=2)+'\n')
	(root/'routes.json').write_bytes(definitions.read_bytes())
	scenarios=[s for s in matrix()['scenarios'] if not a.scenario or s['id'] in a.scenario]
	if not scenarios:raise SystemExit('no matching scenarios')
	for s in scenarios:
		if a.quick:s=dict(s,duration=60,capture=False)
		out=root/s['id']
		if out.exists():raise SystemExit('refusing to reuse result folder '+str(out))
		if not a.already_locked:
			command=['lockf','-k',str(Path.home()/'FerrySim-cache/heavy.lock'),'lockf','-k',str(Path.home()/'FerrySim-cache/unity.lock'),
				os.sys.executable,str(Path(__file__).resolve()),'--already-locked','--player',str(player),'--routes',str(definitions),'--out',str(root),
				'--scenario',s['id'],'--timeout',str(a.timeout)]
			if a.quick:command.append('--quick')
			if a.timing:command.append('--timing')
			status=subprocess.run(command).returncode
			print(s['id'],'harness_exit',status,flush=True)
		else:
			print(s['id'],run(player,definitions,out,s,a.timeout,a.timing),flush=True)


if __name__=='__main__':main()
