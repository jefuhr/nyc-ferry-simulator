using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using FerrySim.Core.Tests.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Env
{
	/// <summary>
	/// The built packs (gitignored, under data/processed/lower-east-river/env) through the core: every day loads with its
	/// current field, and a hull with zero thrust next to each slice station drifts at that station's prediction. Explicit:
	/// they need the pipeline's output, so they run by name and write their numbers to artifacts/current-field.
	/// </summary>
	[TestFixture]
	[Explicit("needs the built packs: PYTHONPATH=data-pipeline uv run --project data-pipeline python -m ferrydata.environment build --region lower-east-river --all")]
	public class RealPackTests
	{
		private static string Environment => Path.Combine(RepoPaths.Root, "data", "processed", "lower-east-river", "env");

		private static string Output
		{
			get
			{
				var directory = Path.Combine(RepoPaths.Root, "artifacts", "current-field");
				Directory.CreateDirectory(directory);
				return directory;
			}
		}

		[Test]
		public void EveryBuiltDay_LoadsWithItsCurrentField()
		{
			var days = new List<string>(Directory.GetDirectories(Path.Combine(Environment, "days")));
			days.Sort(StringComparer.Ordinal);
			var report = new StringBuilder("| Day | Day digest | Field digest | Current stations | Field grid |\n|---|---|---|---:|---|\n");
			foreach (var day in days)
			{
				var pack = EnvironmentPackJson.Load(day);
				Assert.That(pack.CurrentField, Is.Not.Null, day);
				Assert.That(new DataEnvironment(pack, pack.EpochUtc).CurrentField, Is.TypeOf<ChannelCurrentField>());
				var grid = pack.CurrentField.Grids[0];
				report.Append($"| {pack.Date} | `{pack.Digest}` | `{pack.CurrentField.Digest}` | {pack.CurrentStations.Count} | {grid.Rows} × {grid.Columns}, {grid.Slots} slots |\n");
			}

			Assert.That(days.Count, Is.EqualTo(28));
			File.WriteAllText(Path.Combine(Output, "core-load.md"), report.ToString());
			TestContext.Out.WriteLine(report.ToString());
		}

		[TestCase("t-class.json")]
		[TestCase("k-class.json")]
		public void NextToEachSliceStation_AHullWithZeroThrustDriftsAtThatStationsPrediction(string vesselFile)
		{
			var parameters = ManeuverRunner.Load(vesselFile);
			var windy = EnvironmentPackJson.Load(Path.Combine(Environment, "days", "2025-07-14"));
			// The day without its wind, so the drift is the current's alone; a hull drifting in wind also makes leeway.
			var pack = new EnvironmentPack(windy.Region, windy.Date, windy.EpochUtc, windy.CoverageStart, windy.CoverageEnd, windy.Depth, windy.CurrentStations,
				windy.WaterLevelStations, Array.Empty<WindStation>(), windy.Notes, windy.Digest, windy.CurrentField);
			var grid = pack.CurrentField.Grids[0];
			var report = new StringBuilder();
			report.Append($"## {vesselFile}, 2025-07-14 currents, still air\n\n");
			report.Append("| Station | Moment | Station prediction | With the water: off the water where it is, 30 s | Off the station's prediction while within 10 m | Off it after 30 s (distance) | From rest: ground speed at 60 / 150 / 300 s | Water there at 300 s |\n|---|---|---|---:|---:|---|---|---|\n");
			foreach (var station in pack.CurrentStations)
			{
				if (station.North < grid.OriginNorth || station.North > grid.EndNorth || station.East < grid.OriginEast || station.East > grid.EndEast)
				{
					continue;
				}

				foreach (var (moment, packTime) in Peaks(station))
				{
					var start = pack.EpochUtc.AddTicks((long)Math.Round(packTime * TimeSpan.TicksPerSecond));
					var environment = new DataEnvironment(pack, start);
					var predicted = station.Velocity.Sample(packTime);
					var atStation = environment.SampleCurrent(station.North, station.East, 0.0);
					Assert.That((atStation - predicted).Magnitude, Is.LessThan(1e-9), $"{station.Id} {moment}: the field at the station is its prediction");
					var withWater = new VesselDynamicsSimulation(parameters, environment, VesselState.Planar(0.0, station.North, station.East, 0.0, predicted.North, predicted.East));
					double offWater = 0.0, offStation = 0.0, last = 0.0, away = 0.0;
					for (var second = 1; second <= 30; second++)
					{
						ManeuverRunner.Run(withWater, ControlInputs.Neutral, 1.0);
						var state = withWater.State;
						var here = environment.SampleCurrent(state.Position.North, state.Position.East, state.Time);
						offWater = Math.Max(offWater, (state.GroundVelocity - here).Magnitude);
						last = (state.GroundVelocity - station.Velocity.Sample(environment.PackTime(state.Time))).Magnitude;
						away = (state.Position - new HorizontalVector(station.North, station.East)).Magnitude;
						if (away <= 10.0)
						{
							offStation = Math.Max(offStation, last);
						}
					}

					var fromRest = new VesselDynamicsSimulation(parameters, new DataEnvironment(pack, start), VesselState.Planar(0.0, station.North, station.East, 0.0));
					var speeds = new double[3];
					var marks = new[] { 60.0, 150.0, 300.0 };
					var elapsed = 0.0;
					for (var i = 0; i < marks.Length; i++)
					{
						ManeuverRunner.Run(fromRest, ControlInputs.Neutral, marks[i] - elapsed);
						elapsed = marks[i];
						speeds[i] = fromRest.State.GroundVelocity.Magnitude;
					}

					var there = environment.SampleCurrent(fromRest.State.Position.North, fromRest.State.Position.East, fromRest.State.Time);
					report.Append($"| {station.Id} | {moment} | {predicted.Magnitude:F3} m/s toward {Angles.ToDegrees(Angles.WrapTwoPi(predicted.Direction)):F0}° | {offWater:F5} m/s | {offStation:F4} m/s | " +
						$"{last:F4} m/s ({away:F0} m) | {speeds[0]:F3} / {speeds[1]:F3} / {speeds[2]:F3} m/s | {there.Magnitude:F3} m/s |\n");
					Assert.That(offWater, Is.LessThan(0.001), $"{station.Id} {moment}: drifting with the water where the hull is");
					// Away from the station the field turns with the channel, about a degree per 10 m where it bends (NYH1920).
					Assert.That(offStation, Is.LessThan(0.02 * predicted.Magnitude), $"{station.Id} {moment}: within 2 % of the station's prediction next to it");
				}
			}

			report.Append('\n');
			var path = Path.Combine(Output, $"drift-{Path.GetFileNameWithoutExtension(vesselFile)}.md");
			File.WriteAllText(path, report.ToString());
			TestContext.Out.WriteLine(report.ToString());
		}

		// The station's strongest current on the day along its fastest direction, and the strongest the other way.
		private static IEnumerable<(string Moment, double PackTime)> Peaks(CurrentStation station)
		{
			var series = station.Velocity;
			var axis = HorizontalVector.Zero;
			for (var i = 0; i < series.Count; i++)
			{
				if (series.Value(i).Magnitude > axis.Magnitude)
				{
					axis = series.Value(i);
				}
			}

			int along = -1, against = -1;
			double most = double.MinValue, least = double.MaxValue;
			for (var i = 0; i < series.Count; i++)
			{
				if (series.Time(i) < 0.0 || series.Time(i) > 86400.0)
				{
					continue;
				}

				var projection = series.Value(i).North * axis.North + series.Value(i).East * axis.East;
				if (projection > most)
				{
					most = projection;
					along = i;
				}

				if (projection < least)
				{
					least = projection;
					against = i;
				}
			}

			yield return ("strongest along its fastest direction", series.Time(along));
			yield return ("strongest the other way", series.Time(against));
		}
	}
}
