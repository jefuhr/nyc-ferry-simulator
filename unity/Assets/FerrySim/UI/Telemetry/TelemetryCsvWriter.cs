using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Core.Contracts;

namespace FerrySim.UI
{
	/// <summary>
	/// Writes a recording in the CLI's telemetry CSV format (version 1), column for column and digit for
	/// digit as <c>CsvRecorder</c> in core/dotnet/FerrySim.Cli/TelemetryCsv.cs, so runs recorded in Unity
	/// and runs from the CLI load the same way in the debrief and in tools/plot_maneuver.py. A recording
	/// that carries the depth alarm threshold gets it as one extra last column, which the CLI and the plot
	/// tool skip like any extra column.
	/// </summary>
	public static class TelemetryCsvWriter
	{
		public static string Render(TelemetryRecording recording)
		{
			if (recording == null)
			{
				throw new ArgumentNullException(nameof(recording));
			}

			var builder = new StringBuilder(64 + 160 * recording.Count);
			builder.Append("# ").Append(TelemetryCsv.FormatKey).Append(": ").Append(TelemetryCsv.FormatVersion).Append('\n');
			foreach (var pair in recording.Header)
			{
				if (pair.Key == TelemetryCsv.FormatKey)
				{
					continue;
				}

				builder.Append("# ").Append(pair.Key).Append(": ").Append(pair.Value).Append('\n');
			}

			var thresholds = recording.DepthAlarmThreshold;
			builder.Append(string.Join(",", TelemetryCsv.Columns));
			if (thresholds != null)
			{
				builder.Append(',').Append(TelemetryCsv.DepthAlarmThresholdColumn);
			}

			builder.Append('\n');
			for (var i = 0; i < recording.Count; i++)
			{
				AppendRow(builder, recording.Sample(i));
				if (thresholds != null)
				{
					builder.Append(',');
					Append(builder, thresholds[i], "F3", last: true);
				}

				builder.Append('\n');
			}

			return builder.ToString();
		}

		public static void Write(string path, TelemetryRecording recording)
		{
			var directory = Path.GetDirectoryName(Path.GetFullPath(path));
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			File.WriteAllText(path, Render(recording), new UTF8Encoding(false));
		}

		private static void AppendRow(StringBuilder builder, TelemetrySample sample)
		{
			var state = sample.State;
			var telemetry = sample.Telemetry;
			var controls = sample.Controls;
			Append(builder, state.Time, "F3");
			Append(builder, state.North, "F3");
			Append(builder, state.East, "F3");
			Append(builder, Angles.ToDegrees(state.Heading), "F4");
			Append(builder, state.Surge, "F4");
			Append(builder, state.Sway, "F4");
			Append(builder, Angles.ToDegrees(state.YawRate), "F4");
			Append(builder, Units.MetresPerSecondToKnots(telemetry.SpeedOverGround), "F4");
			Append(builder, Angles.ToDegrees(Angles.WrapTwoPi(telemetry.CourseOverGround)), "F3");
			Append(builder, Units.MetresPerSecondToKnots(telemetry.SpeedThroughWater), "F4");
			Append(builder, Units.RadiansPerSecondToRpm(telemetry.Port.EngineSpeed), "F2");
			Append(builder, Units.RadiansPerSecondToRpm(telemetry.Starboard.EngineSpeed), "F2");
			Append(builder, Units.RadiansPerSecondToRpm(telemetry.Port.ShaftSpeed), "F2");
			Append(builder, Units.RadiansPerSecondToRpm(telemetry.Starboard.ShaftSpeed), "F2");
			builder.Append(TelemetryCsv.GearName(telemetry.Port.Gear)).Append(',');
			builder.Append(TelemetryCsv.GearName(telemetry.Starboard.Gear)).Append(',');
			Append(builder, Angles.ToDegrees(telemetry.PortRudderAngle), "F3");
			Append(builder, Angles.ToDegrees(telemetry.StarboardRudderAngle), "F3");
			Append(builder, telemetry.DepthUnderKeel, "F3");
			Append(builder, telemetry.ContactForce, "F1");
			Append(builder, controls.PortLever, "F4");
			Append(builder, controls.StarboardLever, "F4");
			Append(builder, controls.Helm, "F4", last: true);
		}

		private static void Append(StringBuilder builder, double value, string format, bool last = false)
		{
			if (double.IsNaN(value) || double.IsInfinity(value))
			{
				throw new TelemetryCsvException("The recording holds a non-finite value; refusing to write it to the CSV.");
			}

			// .NET Core (the CLI's runtime) keeps the minus sign on values that round to zero ("-0.000");
			// Mono drops it, so the sign is written explicitly to keep both outputs identical.
			if (value < 0.0 || (value == 0.0 && BitConverter.DoubleToInt64Bits(value) < 0))
			{
				builder.Append('-');
				value = -value;
			}

			builder.Append(value.ToString(format, CultureInfo.InvariantCulture));
			if (!last)
			{
				builder.Append(',');
			}
		}
	}
}
