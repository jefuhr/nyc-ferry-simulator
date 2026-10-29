using System;
using FerrySim.HarborWater;
using FerrySim.Sim;
using FerrySim.World;

namespace FerrySim.Harbor
{
	/// <summary>
	/// The harbor scene's one <see cref="IWaterSurface"/>. The vessel placement, the navigation world and the traffic
	/// renderer bind it once at connection; each successful harbor start then hands it that run's
	/// <see cref="HarborWaterService"/> (decision 0005, Water), before the vessel is first placed. Until the first run
	/// it is the canonical datum alone, NAVD88 zero with no tide, so the resident navigation geometry can be anchored;
	/// it never extrapolates and never supplies depth.
	/// </summary>
	public sealed class HarborWaterForwarder : IWaterSurface
	{
		public HarborWaterForwarder(HarborDatum datum)
		{
			Datum = datum ?? throw new ArgumentNullException(nameof(datum));
		}

		/// <summary>The runtime pack's canonical datum, shared by every run.</summary>
		public HarborDatum Datum { get; }

		/// <summary>The current run's water, or null before the first harbor run.</summary>
		public HarborWaterService Current { get; private set; }

		/// <summary>How many runs have bound their water so far.</summary>
		public int Generation { get; private set; }

		/// <summary>Swaps in a new run's water. Called on the runner's harbor <c>RunStarted</c>, before placement.</summary>
		public void Bind(HarborWaterService service)
		{
			Current = service ?? throw new ArgumentNullException(nameof(service));
			Generation++;
		}

		public double EllipsoidalHeight(double north, double east, double time) =>
			Current != null ? Current.EllipsoidalHeight(north, east, time) : Datum.Height(north, east);
	}
}
