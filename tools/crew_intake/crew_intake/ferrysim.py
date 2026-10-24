"""Runs the real ferrysim CLI headlessly and reads steady states off its telemetry CSV."""

import csv
import datetime
import hashlib
import json
import subprocess
from dataclasses import dataclass
from pathlib import Path

CLI_PROJECT = Path("core/dotnet/FerrySim.Cli")
SOURCE_ROOT = Path("core")
SETTLE_WINDOW_S = 20.0
SETTLE_SPEED_KN = 0.01
SETTLE_RPM = 1.0


class FerrySimError(Exception):
	pass


@dataclass
class Steady:
	lever: float
	rpm: float
	stw_kn: float
	settled: bool
	hold_s: float


class FerrySim:
	"""Thin wrapper around `dotnet ferrysim.dll`. Every command it runs is logged to commands.jsonl in the
	output folder. The DLL is built (incrementally) and checked against the source before it is used, so a
	fit never runs an old forward model while claiming to use the repository's CLI."""

	def __init__(self, repo_root: Path, dotnet: str = "dotnet"):
		self.repo_root = Path(repo_root)
		self.dotnet = dotnet
		self.dll: Path | None = None
		self.identity: dict = {}

	def ensure_built(self, build: bool = True) -> Path:
		"""Builds the CLI unless `build` is False (or a DLL was set explicitly), picks the DLL, and refuses one
		that is older than the newest source file under core/. Records the DLL's identity for reports."""
		project = self.repo_root / CLI_PROJECT
		if self.dll is None and build:
			try:
				result = subprocess.run([self.dotnet, "build", str(project), "--nologo", "-v", "quiet"], cwd=self.repo_root, text=True, capture_output=True)
			except OSError as error:
				raise FerrySimError(f"cannot run {self.dotnet}: {error}")
			if result.returncode != 0:
				raise FerrySimError(f"dotnet build of {CLI_PROJECT} failed (exit {result.returncode}): {(result.stderr or result.stdout).strip()}")
		dll = self.dll
		if dll is None:
			candidates = sorted((project / "bin").glob("*/net*/ferrysim.dll"), key=lambda path: path.stat().st_mtime) if (project / "bin").is_dir() else []
			if not candidates:
				raise FerrySimError(f"ferrysim.dll not found under {CLI_PROJECT}/bin; run `dotnet build {CLI_PROJECT}` (or drop --no-build)")
			dll = candidates[-1]
		if not dll.is_file():
			raise FerrySimError(f"{dll} does not exist")
		newest_path, newest_time = self._newest_source()
		if newest_path is not None and dll.stat().st_mtime < newest_time:
			raise FerrySimError(f"{self._relative(dll)} is older than {self._relative(newest_path)}: the CLI build is stale. Run `dotnet build {CLI_PROJECT}` (or drop --no-build) before fitting")
		self.dll = dll
		self.identity = {
			"dll": str(self._relative(dll)),
			"sha256": hashlib.sha256(dll.read_bytes()).hexdigest(),
			"builtAt": _iso(dll.stat().st_mtime),
			"newestSource": str(self._relative(newest_path)) if newest_path is not None else None,
			"newestSourceAt": _iso(newest_time) if newest_path is not None else None,
		}
		return dll

	def _relative(self, path: Path) -> Path:
		try:
			return path.relative_to(self.repo_root)
		except ValueError:
			return path

	def _newest_source(self) -> tuple[Path | None, float]:
		root = self.repo_root / SOURCE_ROOT
		newest: tuple[Path | None, float] = (None, 0.0)
		if not root.is_dir():
			return newest
		for pattern in ("*.cs", "*.csproj", "*.props", "*.targets"):
			for path in root.rglob(pattern):
				if any(part in ("bin", "obj") for part in path.relative_to(root).parts):
					continue
				mtime = path.stat().st_mtime
				if mtime > newest[1]:
					newest = (path, mtime)
		return newest

	def run(self, arguments: list[str], out_dir: Path, label: str) -> subprocess.CompletedProcess:
		dll = self.dll if self.dll is not None and self.identity else self.ensure_built()
		command = [self.dotnet, str(dll), *arguments]
		result = subprocess.run(command, cwd=self.repo_root, text=True, capture_output=True)
		out_dir.mkdir(parents=True, exist_ok=True)
		with (out_dir / "commands.jsonl").open("a") as handle:
			handle.write(json.dumps({"label": label, "argv": command, "exit_code": result.returncode, "stdout": result.stdout.strip(), "stderr": result.stderr.strip()}) + "\n")
		if result.returncode != 0:
			raise FerrySimError(f"ferrysim {label} failed (exit {result.returncode}): {result.stderr.strip() or result.stdout.strip()}")
		return result

	def read_csv(self, path: Path) -> list[dict]:
		with Path(path).open(newline="") as handle:
			lines = [line for line in handle if not line.startswith("#")]
		return list(csv.DictReader(lines))

	def steady_states(self, vessel_file: Path, levers: list[float], out_dir: Path, hold_s: float = 150.0, max_hold_s: float = 1200.0, env: str = "calm", label: str = "steady") -> list[Steady]:
		"""Holds both levers at each position in turn (ascending), long enough for speed and rpm to settle,
		and returns the steady state for every requested lever in the order given. Unsettled holds are
		doubled until max_hold_s, then reported as unsettled rather than guessed."""
		out_dir = Path(out_dir)
		order = sorted(range(len(levers)), key=lambda index: levers[index])
		hold = hold_s
		while True:
			trace = out_dir / f"{label}-trace.csv"
			telemetry = out_dir / f"{label}.csv"
			out_dir.mkdir(parents=True, exist_ok=True)
			with trace.open("w", newline="") as handle:
				handle.write("time_s,port_lever,starboard_lever,helm\n")
				for step, index in enumerate(order):
					handle.write(f"{step * hold:.2f},{levers[index]:.6f},{levers[index]:.6f},0\n")
			duration = hold * len(order)
			self.run(["run", "--vessel", str(vessel_file), "--inputs", str(trace), "--out", str(telemetry), "--duration-s", f"{duration:.2f}", "--sample-hz", "1", "--env", env], out_dir, label)
			rows = self.read_csv(telemetry)
			results: list[Steady | None] = [None] * len(levers)
			all_settled = True
			for step, index in enumerate(order):
				end = (step + 1) * hold
				window = [row for row in rows if end - SETTLE_WINDOW_S - 0.5 <= float(row["time_s"]) <= end + 0.5]
				if len(window) < 2:
					raise FerrySimError(f"no telemetry rows near t={end:g} s in {telemetry}")
				first, last = window[0], window[-1]
				rpm = 0.5 * (float(last["port_engine_rpm"]) + float(last["stbd_engine_rpm"]))
				rpm_first = 0.5 * (float(first["port_engine_rpm"]) + float(first["stbd_engine_rpm"]))
				settled = abs(float(last["stw_kn"]) - float(first["stw_kn"])) <= SETTLE_SPEED_KN and abs(rpm - rpm_first) <= SETTLE_RPM
				all_settled &= settled
				results[index] = Steady(levers[index], rpm, float(last["stw_kn"]), settled, hold)
			if all_settled or hold * 2 > max_hold_s:
				return results  # type: ignore[return-value]
			hold *= 2


def _iso(timestamp: float) -> str:
	return datetime.datetime.fromtimestamp(timestamp, datetime.timezone.utc).isoformat(timespec="seconds")
