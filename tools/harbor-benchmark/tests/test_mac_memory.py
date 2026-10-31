import ctypes
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from mac_memory import MacMemory, Usage


class MacMemoryTests(unittest.TestCase):
	def test_retains_cumulative_paging_and_io_from_the_existing_rusage_read(self):
		monitor = object.__new__(MacMemory)
		monitor.buffer = Usage(pageins=7, diskio_bytesread=100, diskio_byteswritten=200,
			user_time=300, system_time=400, runnable_time=500)
		calls = []
		monitor.call = lambda pid, flavor, buffer: calls.append((pid, flavor)) or 0
		result = monitor.read(123)
		self.assertEqual(calls, [(123, 4)])
		self.assertEqual(result['pageins'], 7)
		self.assertEqual(result['disk_read_bytes'], 100)
		self.assertEqual(result['disk_write_bytes'], 200)
		self.assertEqual(result['user_time_native'], 300)
		self.assertEqual(result['system_time_native'], 400)
		self.assertEqual(result['runnable_time_native'], 500)

	def test_failed_native_read_does_not_become_zero_paging_evidence(self):
		monitor = object.__new__(MacMemory)
		monitor.buffer = Usage()
		def fail(pid, flavor, buffer):
			ctypes.set_errno(1)
			return -1
		monitor.call = fail
		with self.assertRaises(OSError): monitor.read(123)


if __name__ == '__main__':
	unittest.main()
