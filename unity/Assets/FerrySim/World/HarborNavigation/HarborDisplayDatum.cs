using System;
using System.Collections.Generic;
using FerrySim.Core.World;
using FerrySim.UI;
using UnityEngine;

namespace FerrySim.World.HarborNavigation
{
	/// <summary>Presentation datum policy, never an addition to the authoritative pack.</summary>
	public static class HarborDisplayDatum
	{
		// Cached NOAA CO-OPS datums, epoch 1983-2001: MHW minus NAVD88 in metres.
		// Source bytes/hashes and the missing Willets Point datum are recorded in README.md.
		public static double NearestMhw(double north, double east, out string basis)
		{
			var battery = Square(north - 61.520989255491685) + Square(east - 70.1472902628736);
			var kings = Square(north - 12278.750282128694) + Square(east - 21102.357042823896);
			basis = battery <= kings ? "approximate MHW: The Battery 8518750, 1983-2001, NAVD88 +0.597 m" : "approximate MHW: Kings Point 8516945, 1983-2001, NAVD88 +0.986 m";
			return battery <= kings ? .597 : .986;
		}
		private static double Square(double value) => value * value;
	}
}
