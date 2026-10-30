"""Independent Darwin resident/physical-footprint counters, from sys/resource.h rusage_info_v4."""
import ctypes

_FIELDS=('user_time system_time pkg_idle_wkups interrupt_wkups pageins wired_size resident_size phys_footprint proc_start_abstime proc_exit_abstime child_user_time child_system_time child_pkg_idle_wkups child_interrupt_wkups child_pageins child_elapsed_abstime diskio_bytesread diskio_byteswritten cpu_time_qos_default cpu_time_qos_maintenance cpu_time_qos_background cpu_time_qos_utility cpu_time_qos_legacy cpu_time_qos_user_initiated cpu_time_qos_user_interactive billed_system_time serviced_system_time logical_writes lifetime_max_phys_footprint instructions cycles billed_energy serviced_energy interval_max_phys_footprint runnable_time').split()


class Usage(ctypes.Structure):
	_fields_=[('uuid',ctypes.c_uint8*16)]+[(name,ctypes.c_uint64) for name in _FIELDS]


class MacMemory:
	def __init__(self):
		self.library=ctypes.CDLL('/usr/lib/libproc.dylib',use_errno=True)
		self.call=self.library.proc_pid_rusage
		self.call.argtypes=[ctypes.c_int,ctypes.c_int,ctypes.c_void_p]
		self.call.restype=ctypes.c_int
		self.buffer=Usage()

	def read(self,pid):
		if self.call(pid,4,ctypes.byref(self.buffer)):
			error=ctypes.get_errno()
			raise OSError(error,'proc_pid_rusage')
		return {'resident_bytes':int(self.buffer.resident_size),'physical_footprint_bytes':int(self.buffer.phys_footprint),'peak_physical_footprint_bytes':int(self.buffer.lifetime_max_phys_footprint)}
