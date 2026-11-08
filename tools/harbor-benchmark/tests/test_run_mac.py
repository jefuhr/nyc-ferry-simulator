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
