using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Env
{
	/// <summary>
	/// PROVISIONAL baseline current field: each station's velocity is interpolated in time (by component), then the
	/// stations are blended by inverse distance squared. At a station it is that station's velocity exactly, and with one
	/// station it is uniform. It knows nothing of the shoreline, channels or eddies, so between distant stations it is only
	/// a smooth guess. The wave 1 env-research task picks the real method, which replaces this one in wave 2 through
	/// <see cref="ICurrentField"/> without a format change.
	/// <para>
	/// The time-interpolated station velocities are cached for the last pack time asked for, so the samples a simulation
	/// takes along its hull in one step share them. Not thread-safe: one field per simulation thread.
	/// </para>
	/// </summary>
	public sealed class StationBlendCurrentField : ICurrentField
	{
		private readonly VectorSeries[] _series;
		private readonly double[] _stationNorth;
		private readonly double[] _stationEast;
		private readonly double[] _velocityNorth;
		private readonly double[] _velocityEast;
		private double _cachedTime = double.NaN;

		public StationBlendCurrentField(IReadOnlyList<CurrentStation> stations)
		{
			if (stations == null || stations.Count == 0)
			{
				throw new ArgumentException("The station blend needs at least one current station.");
			}

			var count = stations.Count;
			_series = new VectorSeries[count];
			_stationNorth = new double[count];
			_stationEast = new double[count];
			_velocityNorth = new double[count];
			_velocityEast = new double[count];
			for (var i = 0; i < count; i++)
			{
				var station = stations[i] ?? throw new ArgumentException($"Current station {i} is missing.");
				_series[i] = station.Velocity;
				_stationNorth[i] = station.North;
				_stationEast[i] = station.East;
			}
		}

		public HorizontalVector Sample(double north, double east, double packTime)
		{
			if (!(packTime == _cachedTime))
			{
				for (var i = 0; i < _series.Length; i++)
				{
					var velocity = _series[i].Sample(packTime);
					_velocityNorth[i] = velocity.North;
					_velocityEast[i] = velocity.East;
				}

				_cachedTime = packTime;
			}

			InverseDistanceSquared.Blend(north, east, _stationNorth, _stationEast, _velocityNorth, _velocityEast, out var blendNorth, out var blendEast);
			return new HorizontalVector(blendNorth, blendEast);
		}
	}

	/// <summary>Inverse-distance-squared blending of station values, exact at a station and with a single station.</summary>
	internal static class InverseDistanceSquared
	{
		/// <summary>Squared distance, m², within which a point is taken to be at the station.</summary>
		public const double AtStation = 1e-12;

		public static double Blend(double north, double east, double[] stationNorth, double[] stationEast, double[] values)
		{
			if (values.Length == 1)
			{
				return values[0];
			}

			var weights = 0.0;
			var sum = 0.0;
			for (var i = 0; i < values.Length; i++)
			{
				var dn = north - stationNorth[i];
				var de = east - stationEast[i];
				var squared = dn * dn + de * de;
				if (squared <= AtStation)
				{
					return values[i];
				}

				var weight = 1.0 / squared;
				weights += weight;
				sum += weight * values[i];
			}

			return sum / weights;
		}

		public static void Blend(double north, double east, double[] stationNorth, double[] stationEast, double[] first, double[] second,
			out double blendFirst, out double blendSecond)
		{
			if (first.Length == 1)
			{
				blendFirst = first[0];
				blendSecond = second[0];
				return;
			}

			var weights = 0.0;
			var sumFirst = 0.0;
			var sumSecond = 0.0;
			for (var i = 0; i < first.Length; i++)
			{
				var dn = north - stationNorth[i];
				var de = east - stationEast[i];
				var squared = dn * dn + de * de;
				if (squared <= AtStation)
				{
					blendFirst = first[i];
					blendSecond = second[i];
					return;
				}

				var weight = 1.0 / squared;
				weights += weight;
				sumFirst += weight * first[i];
				sumSecond += weight * second[i];
			}

			blendFirst = sumFirst / weights;
			blendSecond = sumSecond / weights;
		}
	}
}
