import json
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import run_mac


class PlayerArgumentTests(unittest.TestCase):
	def test_presentation_switches_survive_locked_child(self):
		with tempfile.TemporaryDirectory() as tmp:
			root = Path(tmp)
			routes = root/'routes.json'
			routes.write_text('{}')
			argv = ['run_mac.py', '--player', '/fixture/player', '--routes', str(routes),
				'--out', str(root/'results'), '--scenario', 'control-none-1',
				'--console-screens', 'on', '--hud-preset', 'everything']
			with patch.object(sys, 'argv', argv), patch.object(run_mac.subprocess, 'run',
				return_value=SimpleNamespace(returncode=0)) as child:
				run_mac.main()
			command = child.call_args.args[0]
			self.assertEqual(command[command.index('--console-screens')+1], 'on')
			self.assertEqual(command[command.index('--hud-preset')+1], 'everything')

	def test_presentation_switches_reach_player_and_result(self):
		with tempfile.TemporaryDirectory() as tmp:
			folder = Path(tmp)/'scenario'
			scenario = next(s for s in run_mac.matrix()['scenarios'] if s['id'] == 'control-none-1')
			with patch.object(run_mac, 'MacMemory'), patch.object(run_mac.platform, 'platform', return_value='fixture OS'), patch.object(run_mac.subprocess, 'Popen') as launch, \
				patch.object(run_mac.os, 'wait4', return_value=(42, 0, SimpleNamespace(ru_maxrss=123456))):
				launch.return_value.pid = 42
				result = run_mac.run(Path('/fixture/player'), Path('/fixture/routes.json'), folder,
					scenario, 60, console_screens='on', hud_preset='everything')
			command = launch.call_args.args[0]
			self.assertEqual(command[command.index('-ferrysim-console-screens')+1], '1')
			self.assertEqual(command[command.index('-ferrysim-hud-preset')+1], 'everything')
			self.assertEqual(result['presentation'], {'console_screens': True, 'hud_preset': 'everything'})

	def test_presentation_defaults_preserve_flat_benchmark(self):
		with tempfile.TemporaryDirectory() as tmp:
			scenario = next(s for s in run_mac.matrix()['scenarios'] if s['id'] == 'control-none-1')
			with patch.object(run_mac, 'MacMemory'), patch.object(run_mac.platform, 'platform', return_value='fixture OS'), patch.object(run_mac.subprocess, 'Popen') as launch, \
				patch.object(run_mac.os, 'wait4', return_value=(42, 0, SimpleNamespace(ru_maxrss=123456))):
				launch.return_value.pid = 42
				result = run_mac.run(Path('/fixture/player'), Path('/fixture/routes.json'), Path(tmp)/'case', scenario, 60)
			self.assertNotIn('-ferrysim-console-screens', launch.call_args.args[0])
			self.assertNotIn('-ferrysim-hud-preset', launch.call_args.args[0])
			self.assertEqual(result['presentation'], {'console_screens': False, 'hud_preset': 'classic'})

	def test_result_records_the_last_player_override(self):
		with tempfile.TemporaryDirectory() as tmp:
			scenario = next(s for s in run_mac.matrix()['scenarios'] if s['id'] == 'control-none-1')
			with patch.object(run_mac, 'MacMemory'), patch.object(run_mac.platform, 'platform', return_value='fixture OS'), \
				patch.object(run_mac.subprocess, 'Popen') as launch, \
				patch.object(run_mac.os, 'wait4', return_value=(42, 0, SimpleNamespace(ru_maxrss=123456))):
				launch.return_value.pid = 42
				result = run_mac.run(Path('/fixture/player'), Path('/fixture/routes.json'), Path(tmp)/'case', scenario, 60,
					console_screens='on', hud_preset='everything', player_args=['-ferrysim-console-screens', '0', '-ferrysim-hud-preset', 'minimal'])
			self.assertEqual(result['presentation'], {'console_screens': False, 'hud_preset': 'minimal'})

	def test_extra_arguments_reach_player_and_process_evidence_in_order(self):
		with tempfile.TemporaryDirectory() as tmp:
			folder = Path(tmp)/'scenario'
			scenario = next(s for s in run_mac.matrix()['scenarios'] if s['id'] == 'control-none-1')
			extra = ['-ferrysim-audio-volume', '0', '-literal', 'value with spaces']
			with patch.object(run_mac, 'MacMemory'), patch.object(run_mac.platform, 'platform', return_value='fixture OS'), \
				patch.object(run_mac.subprocess, 'Popen') as launch, \
				patch.object(run_mac.os, 'wait4', return_value=(42, 0, SimpleNamespace(ru_maxrss=123456))):
				launch.return_value.pid = 42
				result = run_mac.run(Path('/fixture/player'), Path('/fixture/routes.json'), folder,
					scenario, 60, player_args=extra)
			self.assertEqual(launch.call_args.args[0][-len(extra):], extra)
			self.assertEqual(json.loads((folder/'command.json').read_text())[-len(extra):], extra)
			self.assertEqual(result['extra_player_args'], extra)
			self.assertEqual(json.loads((folder/'process.json').read_text())['extra_player_args'], extra)

	def test_extra_arguments_survive_the_locked_child_command(self):
		with tempfile.TemporaryDirectory() as tmp:
			root = Path(tmp)
			routes = root/'routes.json'
			routes.write_text('{}')
			argv = ['run_mac.py', '--player', '/fixture/player', '--routes', str(routes),
				'--out', str(root/'results'), '--scenario', 'control-none-1',
				'--player-arg=-ferrysim-audio-volume', '--player-arg', '0',
				'--player-arg=value with spaces']
			with patch.object(sys, 'argv', argv), \
				patch.object(run_mac.subprocess, 'run', return_value=SimpleNamespace(returncode=0)) as child:
				run_mac.main()
			command = child.call_args.args[0]
			self.assertIn('--already-locked', command)
			self.assertEqual([arg for arg in command if arg.startswith('--player-arg=')],
				['--player-arg=-ferrysim-audio-volume', '--player-arg=0', '--player-arg=value with spaces'])


if __name__ == '__main__':
	unittest.main()
