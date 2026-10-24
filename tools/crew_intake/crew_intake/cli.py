"""Command line: validate, patch, fit-resistance, apply."""

import argparse
import datetime
import json
import shutil
import sys
import tempfile
from pathlib import Path

from crew_intake import answers as answers_module
from crew_intake import fit as fit_module
from crew_intake import modelcheck
from crew_intake import patch as patch_module
from crew_intake.ferrysim import FerrySim, FerrySimError

DEFAULT_OUT = Path("artifacts/crew-intake")
PATCH_OUTPUTS = ("proposed-patch.json", "patch-report.md")
FIT_OUTPUTS = ("resistance-fit.json", "resistance-fit.md")
WORK_DIRS = {"patch": "model-check", "fit-resistance": "work"}
PATCH_COMMANDS = {"proposed-patch.json": "patch", "resistance-fit.json": "fit-resistance"}


def _parser() -> argparse.ArgumentParser:
	parser = argparse.ArgumentParser(prog="intake.py", description="Validates crew questionnaire answers and proposes vessel parameter patches. Patches are proposals: nothing under vessels/ changes unless you run apply with --approved.")
	commands = parser.add_subparsers(dest="command", required=True)

	validate = commands.add_parser("validate", help="check an answers folder (session.json, the CSV tables and the Markdown text answers)")
	validate.add_argument("answers", type=Path, help="folder laid out like docs/crew/answers-template")

	patch = commands.add_parser("patch", help="write a proposed patch for the direct answers and shift timings, after checking the combined model with the ferrysim CLI")
	patch.add_argument("answers", type=Path)
	patch.add_argument("--vessel", type=Path, required=True, help="vessels/<class>.json the answers describe")
	patch.add_argument("--out", type=Path, help="output folder (default artifacts/crew-intake/<sessionId>)")
	_simulator_arguments(patch)

	fit = commands.add_parser("fit-resistance", help="fit hull.residuaryResistanceScale to the rpm-speed table with the ferrysim CLI")
	fit.add_argument("answers", type=Path)
	fit.add_argument("--vessel", type=Path, required=True)
	fit.add_argument("--out", type=Path)
	fit.add_argument("--with-patch", type=Path, help="apply this proposed patch to a temporary copy first (for example the direct-parameter patch with a new idle rpm); the fit then applies only after that patch")
	fit.add_argument("--max-residual-kn", type=float, default=1.5, help="refuse the fit when any row is further than this from the simulator (default 1.5); each row must also be within its own tolerance")
	fit.add_argument("--hold-s", type=float, default=150.0, help="seconds to hold each lever before reading the steady state (default 150; doubled automatically when unsettled)")
	fit.add_argument("--load", choices=("light", "half", "full"), help="fit only the rows at this load when the table mixes loads")
	fit.add_argument("--allow-mixed-conditions", action="store_true", help="fit rows the calm, deep-water model does not represent (mixed loads, wind above the calm limit, unknown or shallow water) anyway; the report says so")
	_simulator_arguments(fit)

	apply = commands.add_parser("apply", help="apply a proposed patch to a vessel file and write the result to --out, after checking the result with the ferrysim CLI")
	apply.add_argument("--patch", type=Path, required=True)
	apply.add_argument("--vessel", type=Path, required=True)
	apply.add_argument("--out", type=Path, required=True, help="where to write the patched file; writing under vessels/ needs --approved")
	apply.add_argument("--approved", action="store_true", help="you have reviewed the patch and want it written under vessels/")
	_simulator_arguments(apply)
	return parser


def _simulator_arguments(command):
	command.add_argument("--dotnet", default="dotnet", help="dotnet executable (default: dotnet on PATH)")
	command.add_argument("--no-build", action="store_true", help="do not run dotnet build first; the existing ferrysim.dll is still refused when it is older than the core source")


def _print_problems(answers: answers_module.AnswerSet):
	for problem in answers.problems:
		print(problem)
	print(f"{len(answers.errors)} error(s), {len(answers.warnings)} warning(s) in {answers.folder}")


def _load_vessel(path: Path) -> dict:
	try:
		return patch_module.load_vessel(path)
	except (OSError, json.JSONDecodeError) as error:
		raise SystemExit(f"error: cannot read vessel file {path}: {error}")


def _out_dir(explicit: Path | None, answers: answers_module.AnswerSet) -> Path:
	out = explicit or DEFAULT_OUT / answers.session.get("sessionId", "unknown-session")
	if patch_module.is_under_vessels(out):
		raise SystemExit(f"error: the output folder {out} is under vessels/; outputs never go there")
	return out


def _simulator(args) -> FerrySim:
	root = patch_module.find_repo_root()
	if root is None:
		raise SystemExit("error: cannot find the repository root (a folder with vessels/, core/ and docs/)")
	sim = FerrySim(root, dotnet=args.dotnet)
	sim.ensure_built(build=not args.no_build)
	return sim


def _read_manifest(out: Path) -> dict:
	path = out / "manifest.json"
	if not path.is_file():
		return {}
	try:
		manifest = json.loads(path.read_text())
	except (OSError, json.JSONDecodeError):
		return {}
	if not isinstance(manifest, dict):
		return {}
	runs = manifest.get("runs")
	return {"sessionId": manifest.get("sessionId"), "runs": runs if isinstance(runs, dict) else {}}


def _start_run(out: Path, command: str, outputs: tuple[str, ...]):
	"""Clears this command's previous outputs, work folder and manifest entry in the folder, so a refused
	rerun can never leave an earlier success beside its refusal report. Other commands' entries stay: patch
	and fit-resistance share the session folder."""
	out.mkdir(parents=True, exist_ok=True)
	for name in outputs:
		(out / name).unlink(missing_ok=True)
	shutil.rmtree(out / WORK_DIRS[command], ignore_errors=True)
	manifest = _read_manifest(out)
	manifest.setdefault("runs", {}).pop(command, None)
	patch_module.write_json(manifest, out / "manifest.json")


def _finish_run(out: Path, command: str, status: str, answers: answers_module.AnswerSet, vessel: dict, outputs: list[str], reasons: list[str]):
	manifest = _read_manifest(out)
	manifest["sessionId"] = answers.session.get("sessionId")
	manifest.setdefault("runs", {})[command] = {
		"status": status,
		"generatedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
		"answersSha256": answers.fingerprint,
		"vesselSha256": patch_module.vessel_fingerprint(vessel),
		"outputs": outputs,
		"reasons": reasons,
	}
	patch_module.write_json(manifest, out / "manifest.json")


def cmd_validate(args) -> int:
	answers = answers_module.load_answers(args.answers)
	_print_problems(answers)
	return 0 if answers.ok else 1


def cmd_patch(args) -> int:
	answers = answers_module.load_answers(args.answers)
	_print_problems(answers)
	if not answers.ok:
		return 1
	vessel = _load_vessel(args.vessel)
	if vessel.get("id") != answers.session.get("vesselClass"):
		print(f"error: session vesselClass is '{answers.session.get('vesselClass')}' but {args.vessel} is '{vessel.get('id')}'")
		return 1
	out = _out_dir(args.out, answers)
	_start_run(out, "patch", PATCH_OUTPUTS)
	proposal = patch_module.build_patch(answers, vessel, str(args.vessel))
	if proposal["modelCheck"]["ok"]:
		try:
			sim = _simulator(args)
			reasons = modelcheck.sanity_run(sim, patch_module.apply_changes(vessel, proposal), out / WORK_DIRS["patch"])
			proposal["modelCheck"] = {"ok": not reasons, "reasons": reasons, "simulated": True}
		except FerrySimError as error:
			proposal["modelCheck"] = {"ok": False, "reasons": [f"the combined model could not be checked with the ferrysim CLI: {error}"], "simulated": False}
	(out / "patch-report.md").write_text(patch_module.patch_report(proposal))
	if not proposal["modelCheck"]["ok"]:
		_finish_run(out, "patch", "refused", answers, vessel, ["patch-report.md"], proposal["modelCheck"]["reasons"])
		print("patch refused: the combined answers do not make a usable vessel")
		for reason in proposal["modelCheck"]["reasons"]:
			print(f"- {reason}")
		print(f"wrote {out / 'patch-report.md'}; no patch was written")
		return 1
	patch_module.write_json(proposal, out / "proposed-patch.json")
	_finish_run(out, "patch", "proposed", answers, vessel, list(PATCH_OUTPUTS), [])
	print(f"proposed {len(proposal['changes'])} change(s), {len(proposal['targets'])} calibration target(s), {len(proposal['refused'])} refused, {len(proposal['warnings'])} warning(s); model check passed with the ferrysim CLI")
	print(f"wrote {out / 'proposed-patch.json'} and {out / 'patch-report.md'} (nothing under vessels/ was changed)")
	return 0


def cmd_fit(args) -> int:
	answers = answers_module.load_answers(args.answers)
	_print_problems(answers)
	if not answers.ok:
		return 1
	vessel = _load_vessel(args.vessel)
	if vessel.get("id") != answers.session.get("vesselClass"):
		print(f"error: session vesselClass is '{answers.session.get('vesselClass')}' but {args.vessel} is '{vessel.get('id')}'")
		return 1
	if args.with_patch:
		try:
			vessel = patch_module.apply_patch(vessel, json.loads(args.with_patch.read_text()))
		except (OSError, json.JSONDecodeError, patch_module.PatchError) as error:
			print(f"error: cannot apply {args.with_patch}: {error}")
			return 1
		print(f"fitting on top of {args.with_patch} (applied to a temporary copy only; the fit will apply only after that patch)")
	out = _out_dir(args.out, answers)
	_start_run(out, "fit-resistance", FIT_OUTPUTS)
	try:
		sim = _simulator(args)
		result = fit_module.fit_resistance(answers, vessel, str(args.vessel), sim, out, max_residual_kn=args.max_residual_kn, hold_s=args.hold_s, load=args.load, allow_mixed_conditions=args.allow_mixed_conditions)
	except FerrySimError as error:
		_finish_run(out, "fit-resistance", "error", answers, vessel, [], [str(error)])
		print(f"error: {error}")
		return 1
	fit_module.write_fit_report(result, out / "resistance-fit.md")
	if result.status == "fitted":
		patch_module.write_json(result.patch, out / "resistance-fit.json")
		_finish_run(out, "fit-resistance", "fitted", answers, vessel, list(FIT_OUTPUTS), [])
		print(f"fitted {fit_module.SCALE_PATH} = {result.scale:.4f} (was {result.initial_scale:g}), RMS residual {result.rms_kn:.2f} kn, {result.runs} simulator runs")
		print(f"wrote {out / 'resistance-fit.json'} and {out / 'resistance-fit.md'} (nothing under vessels/ was changed)")
		return 0
	_finish_run(out, "fit-resistance", "refused", answers, vessel, ["resistance-fit.md"], result.reasons)
	print("fit refused:")
	for reason in result.reasons:
		print(f"- {reason}")
	print(f"wrote {out / 'resistance-fit.md'}; no patch was written")
	return 1


def _manifest_blocks(patch_path: Path) -> str | None:
	"""The reason a manifest beside the patch forbids applying it, or None."""
	manifest_path = patch_path.parent / "manifest.json"
	if not manifest_path.is_file():
		return None
	try:
		manifest = json.loads(manifest_path.read_text())
	except (OSError, json.JSONDecodeError) as error:
		return f"cannot read {manifest_path}: {error}"
	command = PATCH_COMMANDS.get(patch_path.name)
	if command is None:
		return None
	run = (manifest.get("runs") or {}).get(command) if isinstance(manifest, dict) else None
	if not isinstance(run, dict) or run.get("status") not in ("proposed", "fitted") or patch_path.name not in (run.get("outputs") or []):
		status = run.get("status") if isinstance(run, dict) else "no record"
		return f"the manifest {manifest_path} records the latest {command} run in that folder as '{status}' without {patch_path.name}; this patch is stale. Rerun {command}"
	return None


def cmd_apply(args) -> int:
	vessel = _load_vessel(args.vessel)
	try:
		proposal = json.loads(args.patch.read_text())
	except (OSError, json.JSONDecodeError) as error:
		print(f"error: cannot read patch {args.patch}: {error}")
		return 1
	blocked = _manifest_blocks(args.patch)
	if blocked:
		print(f"error: {blocked}")
		return 1
	try:
		patched = patch_module.apply_patch(vessel, proposal)
	except patch_module.PatchError as error:
		print(f"error: {error}")
		return 1
	try:
		sim = _simulator(args)
		with tempfile.TemporaryDirectory(prefix="crew-intake-apply-") as temp:
			reasons = modelcheck.sanity_run(sim, patched, Path(temp))
	except FerrySimError as error:
		print(f"error: the patched vessel could not be checked with the ferrysim CLI: {error}")
		return 1
	if reasons:
		print("error: the patched vessel fails the model check, nothing was written:")
		for reason in reasons:
			print(f"- {reason}")
		return 1
	try:
		patch_module.write_vessel(patched, args.out, approved=args.approved)
	except patch_module.PatchError as error:
		print(f"error: {error}")
		return 1
	print(f"applied {len(proposal.get('changes', []))} change(s) from {args.patch} to {args.vessel}; model check passed with the ferrysim CLI; wrote {args.out}")
	return 0


def main(argv=None) -> int:
	args = _parser().parse_args(argv)
	handler = {"validate": cmd_validate, "patch": cmd_patch, "fit-resistance": cmd_fit, "apply": cmd_apply}[args.command]
	try:
		return handler(args)
	except SystemExit as error:
		if isinstance(error.code, str):
			print(error.code)
			return 1
		raise


if __name__ == "__main__":
	sys.exit(main())
