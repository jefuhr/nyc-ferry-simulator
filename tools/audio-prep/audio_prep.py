#!/usr/bin/env python3
"""Prepare consented local WAVs with Python's standard library and ffmpeg only."""
import argparse
import array
import hashlib
import json
import math
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import wave

PEAK_CEILING = 10 ** (-1 / 20)
SEAM_LIMIT = .002


def run_ffmpeg(arguments, data=None):
	result = subprocess.run(['ffmpeg', '-hide_banner', '-nostdin', '-threads', '1',
		'-filter_threads', '1', *arguments], input=data, capture_output=True)
	if result.returncode:
		# ffmpeg diagnostics can echo private input filenames; never persist them.
		raise ValueError('ffmpeg failed; check the local source format and filter settings')
	return result


def measure_lufs(path):
	result = run_ffmpeg(['-i', str(path), '-af', 'ebur128=peak=true', '-f', 'null', '-'])
	matches = re.findall(r'I:\s+(-?\d+(?:\.\d+)?) LUFS', result.stderr.decode('utf-8', errors='replace'))
	if not matches:
		raise ValueError('ffmpeg ebur128 did not return integrated loudness')
	return float(matches[-1])


def write_pcm(path, samples, rate):
	pcm = array.array('h', (round(x * 32767) for x in samples))
	if sys.byteorder != 'little':
		pcm.byteswap()
	with wave.open(str(path), 'wb') as output:
		output.setparams((1, 2, rate, 0, 'NONE', 'not compressed'))
		output.writeframes(pcm.tobytes())


def make_loop(samples, crossfade):
	"""Overlap tail/head at equal power, then rotate to a low-slope seam in the overlap."""
	if crossfade < 2 or crossfade * 2 >= len(samples):
		raise ValueError('loop crossfade must be at least two frames and less than half the trim')
	blend = array.array('f', (samples[-crossfade+i] * math.cos(math.pi*.5*i/(crossfade-1)) +
		samples[i] * math.sin(math.pi*.5*i/(crossfade-1)) for i in range(crossfade)))
	loop = blend + samples[crossfade:-crossfade]
	# Choose an existing adjacent-sample pair, not a splice between unrelated samples.
	# Rotation preserves the complete overlap and cannot insert a silence notch.
	seam = min(range(1, crossfade), key=lambda i: (abs(loop[i]-loop[i-1]), i))
	return loop[seam:] + loop[:seam], seam


def integer(recipe, name, minimum=0):
	value = recipe[name]
	if type(value) is not int or value < minimum:
		raise ValueError(name + ' must be an integer >= ' + str(minimum))
	return value


def prepare(source, recipe, output):
	"""Write metadata-free PCM16 only after all signal and loudness checks pass."""
	rate = integer(recipe, 'sample_rate', 1)
	source_rate = integer(recipe, 'source_rate', 1)
	start, end = integer(recipe, 'start_frame'), integer(recipe, 'end_frame', 1)
	crossfade, fade = integer(recipe, 'crossfade_frames'), integer(recipe, 'fade_frames')
	target = recipe['target_lufs']
	if not isinstance(target, (int, float)) or not math.isfinite(target) or not -60 <= target <= 0:
		raise ValueError('target_lufs must be finite and between -60 and 0')
	if type(recipe['loop']) is not bool:
		raise ValueError('loop must be a boolean')
	with wave.open(str(source), 'rb') as stream:
		channels = stream.getnchannels()
		if stream.getframerate() != source_rate or not 0 <= start < end <= stream.getnframes():
			raise ValueError('trim or source rate does not match source WAV frames')
		if channels not in (1, 2):
			raise ValueError('only mono or stereo sources are supported')
	expected = math.ceil((end-start) * rate / source_rate)
	if expected < rate * .4:
		raise ValueError('trim must contain at least 400 ms for EBU R128 measurement')
	if recipe['loop']:
		if crossfade < 2 or crossfade * 2 >= expected or fade != 0:
			raise ValueError('invalid loop crossfade or one-shot fade on loop')
	elif crossfade != 0 or fade < 2 or fade * 2 >= expected:
		raise ValueError('one-shots require non-overlapping fades and no loop crossfade')
	filters = [f'atrim=start_sample={start}:end_sample={end}', 'asetpts=PTS-STARTPTS']
	if channels == 2:
		filters.append('pan=mono|c0=0.5*c0+0.5*c1')
	filters.append(f'aresample={rate}:resampler=swr:dither_method=none')
	for field, effect in [('highpass_hz', 'highpass'), ('lowpass_hz', 'lowpass')]:
		frequency = recipe.get(field, 0)
		if not isinstance(frequency, (int, float)) or not math.isfinite(frequency) or not 0 <= frequency < rate/2:
			raise ValueError('invalid ' + field)
		if frequency:
			filters.append(f'{effect}=f={frequency}:p=2')
	decoded = run_ffmpeg(['-v', 'error', '-i', str(source), '-map', '0:a:0', '-af', ','.join(filters),
		'-map_metadata', '-1', '-f', 'f32le', '-acodec', 'pcm_f32le', '-']).stdout
	samples = array.array('f', decoded)
	if sys.byteorder != 'little':
		samples.byteswap()
	if len(samples) != expected or not all(math.isfinite(x) for x in samples):
		raise ValueError('unexpected decoded frame count or non-finite PCM')
	if max(abs(x) for x in samples) < 1e-6:
		raise ValueError('silent source cannot be loudness normalized')
	rotation = 0
	if recipe['loop']:
		samples, rotation = make_loop(samples, crossfade)
	else:
		for i in range(fade):
			gain = math.sin(math.pi*.5*i/(fade-1)) ** 2
			samples[i] *= gain
			samples[-1-i] *= gain
	with tempfile.TemporaryDirectory(prefix='audio-prep-') as scratch:
		meter = Path(scratch) / 'meter.wav'
		# Float WAV input for the meter avoids quantization before gain selection.
		data = array.array('f', samples)
		if sys.byteorder != 'little':
			data.byteswap()
		run_ffmpeg(['-v', 'error', '-f', 'f32le', '-ar', str(rate), '-ac', '1', '-i', 'pipe:0',
			'-map_metadata', '-1', '-c:a', 'pcm_f32le', '-y', str(meter)], data.tobytes())
		before = measure_lufs(meter)
		gain_db = target - before
		gain = 10 ** (gain_db/20)
		samples = array.array('f', (x*gain for x in samples))
		peak = max(abs(x) for x in samples)
		if peak > PEAK_CEILING:
			raise ValueError('target loudness exceeds -1 dBFS peak ceiling; choose a lower target')
		finished = Path(scratch) / 'finished.wav'
		write_pcm(finished, samples, rate)
		lufs = measure_lufs(finished)
		if abs(lufs-target) > .5:
			raise ValueError('finished EBU R128 loudness is outside target +/-0.5 LUFS')
		with wave.open(str(finished), 'rb') as stream:
			pcm = array.array('h', stream.readframes(stream.getnframes()))
		if sys.byteorder != 'little':
			pcm.byteswap()
		seam = abs(pcm[0]-pcm[-1])/32768
		if recipe['loop'] and seam >= SEAM_LIMIT:
			raise ValueError('finished loop seam exceeds 0.002 full scale')
		payload = finished.read_bytes()
	Path(output).write_bytes(payload)
	return dict(frames=len(samples), sample_rate=rate, channels=1, bytes=len(payload),
		sha256=hashlib.sha256(payload).hexdigest(), integrated_lufs=lufs, target_lufs=target,
		gain_db=round(gain_db, 3), peak_dbfs=round(20*math.log10(max(abs(x) for x in pcm)/32768), 3),
		seam_delta=seam, loop_start_sample=0, loop_end_sample=len(samples) if recipe['loop'] else 0,
		crossfade_frames=crossfade, rotation_frames=rotation, fade_frames=fade)


def resolve_source(sheets, source):
	root = Path(sheets).resolve()
	pattern = source['pattern']
	parts = Path(pattern).parts
	if Path(pattern).is_absolute() or '..' in parts or len(parts) != 2 or parts[0] != 'audio' or not pattern.endswith('.wav'):
		raise ValueError('source pattern must stay inside sheets/audio/*.wav')
	matches = sorted(root.glob(pattern))
	if len(matches) != 1:
		raise ValueError('source pattern must resolve to exactly one file')
	file = matches[0]
	if not file.resolve().is_relative_to(root / 'audio') or not file.is_file():
		raise ValueError('source must stay inside the approved sheets/audio directory')
	with file.open('rb') as stream:
		digest = hashlib.file_digest(stream, 'sha256').hexdigest()
	if digest != source['sha256']:
		raise ValueError('source SHA-256 does not match the reviewed recipe')
	return file


def build(sheets, recipes_path, manifest_path, output, report_path):
	recipes = json.loads(Path(recipes_path).read_text())
	manifest = json.loads(Path(manifest_path).read_text())
	if recipes['version'] != 1:
		raise ValueError('unsupported recipe version')
	slots = {slot['id']: slot for slot in manifest['slots']}
	seen, reports = set(), []
	with tempfile.TemporaryDirectory(prefix='audio-prep-batch-') as scratch:
		for recipe in recipes['clips']:
			id = recipe['id']
			if id in seen or id not in slots or not re.fullmatch('[a-z0-9-]+', id):
				raise ValueError('duplicate, unknown or unsafe clip id')
			seen.add(id)
			if recipe.get('placeholder', False):
				continue
			slot = slots[id]
			if slot['file'] != id+'.wav' or slot['loop'] != recipe['loop']:
				raise ValueError('recipe filename or loop mode does not match the manifest slot')
			source = resolve_source(sheets, recipe['source'])
			result = prepare(source, recipe, Path(scratch) / (id+'.wav'))
			result['id'] = id
			reports.append(result)
			slot.update(loopStartSample=result['loop_start_sample'], loopEndSample=result['loop_end_sample'],
				source='crew recording (private), 2026-11', date=recipes['date'],
				edits=f"Recipe {id}: source frames [{recipe['start_frame']}, {recipe['end_frame']}) at {recipe['source_rate']} Hz; "
				f"mono arithmetic downmix; resampled to {recipe['sample_rate']} Hz; highpass {recipe.get('highpass_hz', 0)} Hz, lowpass {recipe.get('lowpass_hz', 0)} Hz; "
				f"{recipe['target_lufs']} LUFS target, ebur128 measured {result['integrated_lufs']} LUFS; "
				f"equal-power overlap {recipe['crossfade_frames']} frames, circular rotation {result['rotation_frames']} frames; "
				f"one-shot fades {recipe['fade_frames']} frames; PCM16, metadata removed; listening review pending")
		# Validate and prepare the entire batch before publishing any clip or provenance.
		Path(output).mkdir(parents=True, exist_ok=True)
		for result in reports:
			name = result['id'] + '.wav'
			(Path(output)/name).write_bytes((Path(scratch)/name).read_bytes())
	Path(manifest_path).write_text(json.dumps(manifest, indent='\t')+'\n')
	version = subprocess.run(['ffmpeg', '-version'], capture_output=True, text=True, check=True).stdout.splitlines()[0]
	Path(report_path).write_text(json.dumps(dict(ffmpeg=version, clips=reports, total_wav_bytes=sum(r['bytes'] for r in reports)), indent=2)+'\n')
	return reports


def main():
	parser = argparse.ArgumentParser(description=__doc__)
	parser.add_argument('--sheets', type=Path, required=True)
	parser.add_argument('--recipes', type=Path, default=Path(__file__).with_name('recipes.json'))
	parser.add_argument('--manifest', type=Path, default=Path('unity/Assets/FerrySim/Audio/clips.json'))
	parser.add_argument('--output', type=Path, default=Path('unity/Assets/FerrySim/Audio/Clips'))
	parser.add_argument('--report', type=Path, required=True)
	args = parser.parse_args()
	try:
		reports = build(args.sheets, args.recipes, args.manifest, args.output, args.report)
	except (ValueError, KeyError, wave.Error, OSError) as error:
		# Do not expose OS errors containing private filenames.
		parser.exit(1, ('audio-prep: ' + str(error) if isinstance(error, ValueError) else 'audio-prep: invalid recipe or unavailable input/output')+'\n')
	print(f"Prepared {len(reports)} clips; {sum(r['bytes'] for r in reports)} PCM WAV bytes")


if __name__ == '__main__':
	main()
