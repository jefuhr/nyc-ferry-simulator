import json

import matplotlib.pyplot as plt
import pandas as pd
import pytest


@pytest.mark.parametrize('crossing_s', [24.5, 31.2])
def test_cadence_plot_reads_baseline_crossing_from_model_summary(tmp_path, crossing_s):
	from calibration.__main__ import plot_cadence
	path = tmp_path / 'model-summary.json'
	path.write_text(json.dumps({'sensitivity': [
		dict(factor=.8, time_to_25_kn_s=20.8),
		dict(factor=1., time_to_25_kn_s=crossing_s),
		dict(factor=1.2, time_to_25_kn_s=37.1)]}))
	fig, ax = plt.subplots()
	try:
		plot_cadence(ax, pd.Series([1., 5., 60., 90.]), path)
		line, = ax.lines
		assert list(line.get_xdata()) == [crossing_s, crossing_s]
		assert line.get_label() == f'Model 0–25 kn: {crossing_s:.1f} s'
	finally:
		plt.close(fig)


def test_cadence_plot_missing_summary_explains_prerequisite(tmp_path):
	from calibration.__main__ import plot_cadence
	fig, ax = plt.subplots()
	try:
		with pytest.raises(FileNotFoundError, match='calibration.simulator'):
			plot_cadence(ax, pd.Series([60.]), tmp_path / 'missing.json')
	finally:
		plt.close(fig)
