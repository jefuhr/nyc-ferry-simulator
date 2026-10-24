"""Run the real ferrysim CLI against temporary sensitivity variants, never fitted files."""

import json
from pathlib import Path
import subprocess

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd

from ferrydata import paths


def run():
	root = paths.REPO_ROOT
	out = root / 'artifacts/calibration'
	out.mkdir(parents=True, exist_ok=True)
	vessel = json.loads((root / 'vessels/t-class.json').read_text())
	commands = []
	def cli(arguments, name):
		command = ['dotnet', 'run', '--no-build', '--project', 'core/dotnet/FerrySim.Cli', '--', *arguments]
		result = subprocess.run(command, cwd=root, text=True, capture_output=True)
		(out / f'{name}.txt').write_text(result.stdout + result.stderr)
		commands.append(dict(argv=command, exit_code=result.returncode))
		(out / 'simulator-commands.json').write_text(json.dumps(commands, indent='\t') + '\n')
		if result.returncode:
			raise RuntimeError(f'{name} failed: {result.stderr}')
		return pd.read_csv(out / f'{name}.csv', comment='#')
	results, curves = [], []
	for factor in [.8, 1., 1.2]:
		variant = json.loads(json.dumps(vessel))
		scale = variant['hull']['residuaryResistanceScale']['value'] * factor
		variant['hull']['residuaryResistanceScale']['value'] = scale
		variant['hull']['residuaryResistanceScale']['note'] = 'Sensitivity experiment only; not an AIS fit.'
		parameter_file = out / f't-resistance-{factor:.1f}.json'
		parameter_file.write_text(json.dumps(variant, indent='\t') + '\n')
		name = f't-resistance-{factor:.1f}-acceleration'
		frame = cli(['maneuver', 'acceleration', '--vessel', str(parameter_file), '--duration-s', '180', '--out', str(out / f'{name}.csv')], name)
		crossing = frame.loc[frame.stw_kn >= 25, 'time_s']
		results.append(dict(resistance_scale=scale, factor=factor, steady_speed_kn=float(frame.stw_kn.iloc[-1]),
			time_to_25_kn_s=float(crossing.iloc[0]) if len(crossing) else None))
		curves.append((scale, frame))
	for maneuver in ['turning-circle', 'crash-stop']:
		name = f't-baseline-{maneuver}'
		frame = cli(['maneuver', maneuver, '--vessel', 'vessels/t-class.json', '--out', str(out / f'{name}.csv')], name)
		if maneuver == 'turning-circle':
			tail = frame.iloc[-max(1,len(frame)//4):]
			turn = dict(mean_final_surge_kn=float(tail.stw_kn.mean()), speed_loss_fraction=float(1-tail.stw_kn.mean()/25),
				mean_final_yaw_deg_s=float(tail.yaw_rate_degps.mean()))
	(out / 'idle-inputs.csv').write_text('time_s,port_lever,starboard_lever,helm\n0,0.2,0.2,0\n300,0.2,0.2,0\n')
	idle = cli(['run', '--vessel', 'vessels/t-class.json', '--inputs', str(out/'idle-inputs.csv'),
		'--out', str(out/'t-baseline-idle.csv')], 't-baseline-idle')
	(out / 'model-summary.json').write_text(json.dumps(dict(sensitivity=results, turning=turn,
		idle_final_stw_kn=float(idle.stw_kn.iloc[-1]), parameters_changed=[]), indent='\t')+'\n')
	fig, axes = plt.subplots(1, 2, figsize=(12, 4), layout='constrained')
	for scale, frame in curves:
		axes[0].plot(frame.time_s, frame.stw_kn, label=f'Resistance scale {scale:.2f}')
	axes[0].set(xlim=(0,90), xlabel='Seconds from full-ahead command', ylabel='Surge through water (kn)',
		title='Simulator sensitivity; no variant accepted as a fit')
	axes[0].axhline(25,color='grey',linestyle='--')
	axes[0].legend()
	turn_frame = pd.read_csv(out/'t-baseline-turning-circle.csv',comment='#')
	axes[1].plot(turn_frame.time_s-turn_frame.time_s.iloc[0],turn_frame.stw_kn,label='Before = after (unchanged)')
	axes[1].set(xlabel='Seconds from trace start (includes lead-in)',ylabel='Surge through water (kn)',title='T-class hard-over turn, calm deep water')
	axes[1].legend()
	fig.savefig(out/'simulator-sensitivity.png',dpi=160)
	plt.close(fig)
	print(json.dumps(results, indent=2))


if __name__ == '__main__':
	run()
