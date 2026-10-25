using System;
using FerrySim.Sim;
using FerrySim.World;

namespace FerrySim.Slice
{
	/// <summary>
	/// Keeps the world's drawn water at the run's tide. Just before the runner places the vessel (every frame, and at every
	/// run start) it sets <see cref="WaterSurface.WaterLevelNavd88"/> to the pack's water level at the vessel's reference point
	/// at the run's time (<see cref="VesselSimulationRunner.Environment"/>), so the hull (whose height
	/// <see cref="WorldWaterHeight"/> reads from the drawn level), the water and the floating barges (<c>FloatOnWater</c>)
	/// agree in the same frame. A calm or uniform run has no tide: the water goes back to <see cref="BaseLevel"/>, the level
	/// the scene was built with (NAVD88 0.0 m in the slice). Allocation-free per frame.
	/// </summary>
	public sealed class TideFollower : IDisposable
	{
		private readonly VesselSimulationRunner _runner;
		private readonly WaterSurface _water;
		private bool _disposed;

		public TideFollower(VesselSimulationRunner runner, WaterSurface water)
		{
			_runner = runner != null ? runner : throw new ArgumentNullException(nameof(runner));
			_water = water != null ? water : throw new ArgumentNullException(nameof(water));
			BaseLevel = water.WaterLevelNavd88;
			_runner.BeforePlacement += Follow;
		}

		/// <summary>The drawn level, m above NAVD88, for runs without a tide.</summary>
		public double BaseLevel { get; }

		/// <summary>Sets the drawn level for the runner's current run and time; the runner calls it before each placement.</summary>
		public void Follow()
		{
			if (_disposed || _water == null || _runner.RunEnvironment == null)
			{
				return;
			}

			var level = _runner.RunEnvironment.HasTide ? _runner.Environment.WaterLevelNavd88 : BaseLevel;
			if (!double.IsNaN(level) && !double.IsInfinity(level) && level != _water.WaterLevelNavd88)
			{
				_water.WaterLevelNavd88 = level;
			}
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			if (_runner != null)
			{
				_runner.BeforePlacement -= Follow;
			}
		}
	}
}
