using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;

namespace FerrySim.Cli
{
	/// <summary>One computed number. <see cref="Value"/> is null when the maneuver never got far enough to define it.</summary>
	public sealed class MetricValue
	{
		public MetricValue(string key, string label, string unit, double? value)
		{
			Key = key;
			Label = label;
			Unit = unit;
			Value = value;
		}

		/// <summary>Snake-case name with the unit suffix, used in JSON.</summary>
		public string Key { get; }

		public string Label { get; }

		public string Unit { get; }

		public double? Value { get; }
	}

	public sealed class MetricsReport
	{
		public string Vessel { get; set; }

		public string Maneuver { get; set; }

		public string Environment { get; set; }

		public string Model { get; set; }

		public List<KeyValuePair<string, string>> Parameters { get; } = new List<KeyValuePair<string, string>>();

		public List<MetricValue> Metrics { get; } = new List<MetricValue>();

		public List<string> Notes { get; } = new List<string>();

		public MetricValue Find(string key)
		{
			foreach (var metric in Metrics)
			{
				if (metric.Key == key)
				{
					return metric;
				}
			}

			return null;
		}
	}

	/// <summary>
	/// IMO-style maneuver metrics computed from a recorded CSV alone. Positions are relative to the
	/// maneuver start (the row at <c>maneuver_start_s</c>) and resolved along and across the heading
	/// there; "lateral" is positive to starboard. Everything is over the ground, so run in calm water
	/// when comparing with the IMO criteria.
	/// </summary>
	public static class ManeuverMetrics
	{
		public static MetricsReport Compute(TelemetryTable table)
		{
			if (table.RowCount < 2)
			{
				throw new CliException("The CSV has fewer than two rows; nothing to summarize.");
			}

			var maneuver = table.HeaderValue("maneuver", "unknown");
			var report = new MetricsReport
			{
				Vessel = table.HeaderValue("vessel", "unknown"),
				Maneuver = maneuver,
				Environment = table.HeaderValue("environment", "unknown"),
				Model = table.HeaderValue("model", "unknown"),
			};
			foreach (var pair in table.Header)
			{
				if (pair.Key.StartsWith("param.", StringComparison.Ordinal))
				{
					report.Parameters.Add(new KeyValuePair<string, string>(pair.Key.Substring(6), pair.Value));
				}
			}

			var track = new Track(table);
			switch (maneuver)
			{
				case "turning-circle":
					TurningCircle(track, report);
					break;
				case "zigzag":
					Zigzag(track, table, report);
					break;
				case "crash-stop":
				case "stopping":
					Stop(track, table, report);
					break;
				case "acceleration":
					Acceleration(track, report);
					break;
				case "pivot":
					Pivot(track, report);
					break;
				default:
					General(track, report);
					report.Notes.Add($"No maneuver metrics are defined for '{maneuver}'; showing general figures.");
					break;
			}

			return report;
		}

		private static void TurningCircle(Track track, MetricsReport report)
		{
			var sign = track.TurnSign();
			var turned = new double[track.Count];
			for (var i = 0; i < turned.Length; i++)
			{
				turned[i] = sign * track.HeadingChange[i];
			}

			var at90 = track.Cross(turned, 90.0);
			var at180 = track.Cross(turned, 180.0);
			var at360 = track.Cross(turned, 360.0);
			report.Metrics.Add(new MetricValue("advance_m", "Advance (at 90 deg)", "m", at90.HasValue ? track.Along(at90.Value) : (double?)null));
			report.Metrics.Add(new MetricValue("transfer_m", "Transfer (at 90 deg)", "m", at90.HasValue ? sign * track.Lateral(at90.Value) : (double?)null));
			report.Metrics.Add(new MetricValue("tactical_diameter_m", "Tactical diameter (transfer at 180 deg)", "m", at180.HasValue ? sign * track.Lateral(at180.Value) : (double?)null));
			report.Metrics.Add(new MetricValue("time_to_90_deg_s", "Time to 90 deg heading change", "s", at90.HasValue ? track.TimeAt(at90.Value) : (double?)null));
			report.Metrics.Add(new MetricValue("time_to_180_deg_s", "Time to 180 deg heading change", "s", at180.HasValue ? track.TimeAt(at180.Value) : (double?)null));
			report.Metrics.Add(new MetricValue("time_to_360_deg_s", "Time to 360 deg heading change", "s", at360.HasValue ? track.TimeAt(at360.Value) : (double?)null));
			report.Notes.Add(sign > 0 ? "Turned to starboard." : "Turned to port; transfer is positive toward the turn.");
			if (!at360.HasValue)
			{
				report.Notes.Add($"The record ends at {sign * track.HeadingChange[track.Count - 1]:F0} deg of heading change; metrics beyond that are not available.");
			}
		}

		private static void Zigzag(Track track, TelemetryTable table, MetricsReport report)
		{
			var check = table.HeaderDouble("param.check_deg", double.NaN);
			double? first = null, second = null;
			var reversals = new List<int>();
			var turnSign = 0.0;
			for (var i = 0; i < track.Count; i++)
			{
				var side = Math.Sign(track.Helm[i]);
				if (side == 0)
				{
					continue;
				}

				if (turnSign == 0.0)
				{
					turnSign = side;
				}
				else if (side != Math.Sign(turnSign) * ((reversals.Count % 2 == 0) ? 1 : -1))
				{
					reversals.Add(i);
				}
			}

			if (double.IsNaN(check))
			{
				report.Notes.Add("The CSV header has no param.check_deg; overshoots cannot be computed.");
			}
			else if (turnSign == 0.0)
			{
				report.Notes.Add("The helm never moved off amidships; overshoots cannot be computed.");
			}
			else
			{
				if (reversals.Count >= 2)
				{
					first = Math.Max(0.0, PeakBeyond(track, turnSign, reversals[0], reversals[1]) - check);
				}

				if (reversals.Count >= 3)
				{
					second = Math.Max(0.0, PeakBeyond(track, -turnSign, reversals[1], reversals[2]) - check);
				}
			}

			report.Metrics.Add(new MetricValue("first_overshoot_deg", "First overshoot", "deg", first));
			report.Metrics.Add(new MetricValue("second_overshoot_deg", "Second overshoot", "deg", second));
			report.Notes.Add($"Helm reversals found: {reversals.Count}. The first overshoot needs two, the second needs three.");
		}

		private static double PeakBeyond(Track track, double sign, int from, int to)
		{
			var peak = double.NegativeInfinity;
			for (var i = from; i <= to; i++)
			{
				peak = Math.Max(peak, Math.Sign(sign) * track.HeadingChange[i]);
			}

			return peak;
		}

		private static void Stop(Track track, TelemetryTable table, MetricsReport report)
		{
			var threshold = table.HeaderDouble("param.stop_speed_mps", StopController.StopSpeedMetresPerSecond);
			double? headReach = null, lateral = null, maxLateral = null, trackReach = null, timeToStop = null;
			if (track.Stw[0] <= threshold)
			{
				report.Notes.Add("The vessel was already stopped at the start of the maneuver.");
			}
			else
			{
				var below = new double[track.Count];
				for (var i = 0; i < below.Length; i++)
				{
					below[i] = -track.Stw[i];
				}

				var stop = track.Cross(below, -threshold);
				if (stop.HasValue)
				{
					headReach = track.Along(stop.Value);
					lateral = track.Lateral(stop.Value);
					timeToStop = track.TimeAt(stop.Value);
					var whole = (int)Math.Floor(stop.Value);
					var maximum = Math.Abs(track.LateralAt[whole]);
					var path = 0.0;
					for (var i = 1; i <= whole; i++)
					{
						maximum = Math.Max(maximum, Math.Abs(track.LateralAt[i]));
						path += track.Step(i);
					}

					var fraction = stop.Value - whole;
					if (whole + 1 < track.Count)
					{
						path += fraction * track.Step(whole + 1);
					}

					maxLateral = maximum;
					trackReach = path;
				}
				else
				{
					report.Notes.Add($"The speed through water never fell to {threshold:F2} m/s; the record is too short.");
				}
			}

			report.Metrics.Add(new MetricValue("head_reach_m", "Head reach", "m", headReach));
			report.Metrics.Add(new MetricValue("track_reach_m", "Track reach", "m", trackReach));
			report.Metrics.Add(new MetricValue("lateral_deviation_m", "Lateral deviation at stop (+ starboard)", "m", lateral));
			report.Metrics.Add(new MetricValue("max_lateral_deviation_m", "Maximum lateral deviation", "m", maxLateral));
			report.Metrics.Add(new MetricValue("time_to_stop_s", "Time to stop", "s", timeToStop));
			report.Notes.Add($"Stopped means speed through water at or below {threshold:F2} m/s.");
		}

		private static void Acceleration(Track track, MetricsReport report)
		{
			var duration = track.Time[track.Count - 1] - track.Time[0];
			var window = Math.Min(20.0, 0.25 * duration);
			var steady = track.MeanOver(track.Stw, track.Time[track.Count - 1] - window, track.Time[track.Count - 1]);
			var earlier = track.MeanOver(track.Stw, track.Time[track.Count - 1] - 2.0 * window, track.Time[track.Count - 1] - window);
			double? steadyKn = null, t90 = null, distance90 = null;
			var settled = false;
			if (!double.IsNaN(steady) && steady > 0.05)
			{
				steadyKn = Units.MetresPerSecondToKnots(steady);
				var at90 = track.Cross(track.Stw, 0.9 * steady);
				if (at90.HasValue)
				{
					t90 = track.TimeAt(at90.Value);
					distance90 = track.Along(at90.Value);
				}

				settled = !double.IsNaN(earlier) && Math.Abs(steady - earlier) <= 0.01 * steady;
			}
			else
			{
				report.Notes.Add("The vessel never gained speed; steady speed and time to 90% are undefined.");
			}

			report.Metrics.Add(new MetricValue("steady_speed_kn", "Steady speed through water", "kn", steadyKn));
			report.Metrics.Add(new MetricValue("time_to_90_percent_speed_s", "Time to 90% of steady speed", "s", t90));
			report.Metrics.Add(new MetricValue("distance_to_90_percent_speed_m", "Distance to 90% of steady speed", "m", distance90));
			report.Notes.Add($"Steady speed is the mean speed through water over the last {window:F0} s of the record.");
			if (steadyKn.HasValue && !settled)
			{
				report.Notes.Add("The speed was still changing at the end of the record; the steady speed and time to 90% are provisional.");
			}
		}

		private static void Pivot(Track track, MetricsReport report)
		{
			var duration = track.Time[track.Count - 1] - track.Time[0];
			var window = Math.Max(Math.Min(5.0, duration), 0.25 * duration);
			var rate = track.MeanOver(track.YawRate, track.Time[track.Count - 1] - window, track.Time[track.Count - 1]);
			var last = track.Count - 1;
			var maximum = 0.0;
			for (var i = 0; i < track.Count; i++)
			{
				maximum = Math.Max(maximum, Math.Sqrt(track.AlongAt[i] * track.AlongAt[i] + track.LateralAt[i] * track.LateralAt[i]));
			}

			var net = Math.Sqrt(track.AlongAt[last] * track.AlongAt[last] + track.LateralAt[last] * track.LateralAt[last]);
			report.Metrics.Add(new MetricValue("yaw_rate_degps", "Yaw rate (+ to starboard)", "deg/s", rate));
			report.Metrics.Add(new MetricValue("heading_change_deg", "Heading change", "deg", track.HeadingChange[last]));
			report.Metrics.Add(new MetricValue("drift_m", "Drift (largest distance from the start point)", "m", maximum));
			report.Metrics.Add(new MetricValue("net_displacement_m", "Net displacement", "m", net));
			report.Notes.Add($"Yaw rate is the mean over the last {window:F0} s of the record.");
		}

		private static void General(Track track, MetricsReport report)
		{
			var last = track.Count - 1;
			var distance = 0.0;
			var maximum = double.NegativeInfinity;
			for (var i = 0; i < track.Count; i++)
			{
				if (i > 0)
				{
					distance += track.Step(i);
				}

				maximum = Math.Max(maximum, track.Stw[i]);
			}

			report.Metrics.Add(new MetricValue("duration_s", "Duration", "s", track.Time[last] - track.Time[0]));
			report.Metrics.Add(new MetricValue("distance_m", "Distance travelled", "m", distance));
			report.Metrics.Add(new MetricValue("max_speed_through_water_kn", "Maximum speed through water", "kn", Units.MetresPerSecondToKnots(maximum)));
			report.Metrics.Add(new MetricValue("heading_change_deg", "Heading change", "deg", track.HeadingChange[last]));
		}

		/// <summary>The recorded track from the maneuver start on, resolved relative to the starting position and heading.</summary>
		private sealed class Track
		{
			private readonly double[] _north;
			private readonly double[] _east;

			public Track(TelemetryTable table)
			{
				var start = table.HeaderDouble("maneuver_start_s", table.Time[0]);
				var first = 0;
				while (first < table.RowCount - 1 && table.Time[first] < start - 1e-6)
				{
					first++;
				}

				Count = table.RowCount - first;
				Time = new double[Count];
				Stw = new double[Count];
				YawRate = new double[Count];
				Helm = new double[Count];
				HeadingChange = new double[Count];
				AlongAt = new double[Count];
				LateralAt = new double[Count];
				_north = new double[Count];
				_east = new double[Count];

				var heading0 = Angles.ToRadians(table.HeadingDeg[first]);
				var cos = Math.Cos(heading0);
				var sin = Math.Sin(heading0);
				var unwrapped = 0.0;
				for (var i = 0; i < Count; i++)
				{
					var row = first + i;
					Time[i] = table.Time[row];
					Stw[i] = Units.KnotsToMetresPerSecond(table.StwKn[row]);
					YawRate[i] = table.YawRateDegps[row];
					Helm[i] = table.Helm[row];
					_north[i] = table.North[row];
					_east[i] = table.East[row];
					if (i > 0)
					{
						unwrapped += Angles.WrapPi(Angles.ToRadians(table.HeadingDeg[row] - table.HeadingDeg[row - 1]));
					}

					HeadingChange[i] = Angles.ToDegrees(unwrapped);
					var dn = _north[i] - _north[0];
					var de = _east[i] - _east[0];
					AlongAt[i] = dn * cos + de * sin;
					LateralAt[i] = -dn * sin + de * cos;
				}
			}

			public int Count { get; }

			public double[] Time { get; }

			/// <summary>Speed through water, m/s, negative astern.</summary>
			public double[] Stw { get; }

			public double[] YawRate { get; }

			public double[] Helm { get; }

			/// <summary>Cumulative heading change, degrees, positive to starboard, not wrapped.</summary>
			public double[] HeadingChange { get; }

			public double[] AlongAt { get; }

			public double[] LateralAt { get; }

			/// <summary>+1 if the vessel ended up turned to starboard, -1 to port.</summary>
			public double TurnSign()
			{
				var best = 0.0;
				for (var i = 0; i < Count; i++)
				{
					if (Math.Abs(HeadingChange[i]) > Math.Abs(best))
					{
						best = HeadingChange[i];
					}
				}

				return best < 0.0 ? -1.0 : 1.0;
			}

			/// <summary>Fractional row index where <paramref name="values"/> first rises to <paramref name="threshold"/>.</summary>
			public double? Cross(double[] values, double threshold)
			{
				if (values[0] >= threshold)
				{
					return 0.0;
				}

				for (var i = 1; i < Count; i++)
				{
					if (values[i] >= threshold)
					{
						return i - 1 + (threshold - values[i - 1]) / (values[i] - values[i - 1]);
					}
				}

				return null;
			}

			public double Interpolate(double[] values, double index)
			{
				var whole = (int)Math.Floor(index);
				if (whole >= Count - 1)
				{
					return values[Count - 1];
				}

				return values[whole] + (index - whole) * (values[whole + 1] - values[whole]);
			}

			public double Along(double index) => Interpolate(AlongAt, index);

			public double Lateral(double index) => Interpolate(LateralAt, index);

			/// <summary>Seconds since the maneuver start at a fractional row index.</summary>
			public double TimeAt(double index) => Interpolate(Time, index) - Time[0];

			/// <summary>Distance between row i - 1 and row i.</summary>
			public double Step(int i)
			{
				var dn = _north[i] - _north[i - 1];
				var de = _east[i] - _east[i - 1];
				return Math.Sqrt(dn * dn + de * de);
			}

			public double MeanOver(double[] values, double from, double to)
			{
				var sum = 0.0;
				var n = 0;
				for (var i = 0; i < Count; i++)
				{
					if (Time[i] >= from - 1e-9 && Time[i] <= to + 1e-9)
					{
						sum += values[i];
						n++;
					}
				}

				return n == 0 ? double.NaN : sum / n;
			}
		}
	}
}
