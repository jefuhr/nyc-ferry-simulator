using System;
using System.Collections.Generic;
using FerrySim.Core.Contracts;
using FerrySim.Core.Env;
using FerrySim.Sim;

namespace FerrySim.Slice
{
	public sealed partial class SliceScenarioCatalog
	{
		private readonly Dictionary<string, DataEnvironment> _depthDays = new Dictionary<string, DataEnvironment>(StringComparer.Ordinal);

		/// <summary>Minimum sampled water depth over the contact hull, on a 0.25 m lattice plus every outline edge.</summary>
		public bool TryGetStartDepth(ScenarioChoice choice, out ScenarioStartDepth depth)
		{
			depth = default;
			if (!TryCreateCandidate(choice, out var settings, out _)) return false;
			return TryGetStartDepth(choice, settings, out depth);
		}

		private bool TryGetStartDepth(ScenarioChoice choice, ScenarioSettings settings, out ScenarioStartDepth depth)
		{
			depth = default;
			if (!settings.environment.IsPack) return false;
			if (!_depthDays.TryGetValue(choice.Date, out var environment))
			{
				var pack = _packs.Load(settings.environment.pack);
				environment = new DataEnvironment(pack, pack.EpochUtc);
				_depthDays.Add(choice.Date, environment);
			}
			var vessel = Vessel(choice.VesselId);
			var position = settings.StartPosition;
			var outline = FaceClearance.WorldOutline(vessel, position.North, position.East, settings.StartHeading);
			var minimum = MinimumStartDepth(environment, outline, choice.LocalMinutes * 60.0);
			if (double.IsNaN(minimum)) return false;
			depth = new ScenarioStartDepth(minimum, (vessel.Particulars.MaxDraft ?? vessel.Particulars.Draft).Value);
			return true;
		}

		/// <summary>
		/// Samples the actual bilinear bed and spatial tide under the hull. A quarter-metre world lattice,
		/// every depth-grid node inside the hull, and edges at no more than 0.25 m spacing guard narrow shoals.
		/// This is a sampled minimum, not a continuous bathymetric survey. Unknown bed returns NaN.
		/// </summary>
		public static double MinimumStartDepth(DataEnvironment environment, HorizontalVector[] outline, double time)
		{
			const double spacing = 0.25;
			var minN = double.PositiveInfinity; var maxN = double.NegativeInfinity;
			var minE = double.PositiveInfinity; var maxE = double.NegativeInfinity;
			foreach (var p in outline)
			{
				minN = Math.Min(minN, p.North); maxN = Math.Max(maxN, p.North);
				minE = Math.Min(minE, p.East); maxE = Math.Max(maxE, p.East);
			}
			var minimum = double.PositiveInfinity;
			void Sample(double n, double e)
			{
				if (environment.SampleBedElevation(n, e, out var bed) != BedSample.Known) { minimum = double.NaN; return; }
				minimum = Math.Min(minimum, environment.SampleWaterLevel(n, e, time) - bed);
			}
			bool Inside(double n, double e)
			{
				var inside = false;
				for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
				{
					var a = outline[i]; var b = outline[j];
					if ((a.East > e) != (b.East > e) && n < (b.North - a.North) * (e - a.East) / (b.East - a.East) + a.North) inside = !inside;
				}
				return inside;
			}
			for (var n = Math.Ceiling(minN / spacing) * spacing; n <= maxN; n += spacing)
				for (var e = Math.Ceiling(minE / spacing) * spacing; e <= maxE; e += spacing)
					if (Inside(n, e)) Sample(n, e);
			foreach (var grid in environment.Pack.Depth.Grids)
			{
				var firstRow = Math.Max(0, (int)Math.Ceiling((minN - grid.OriginNorth) / grid.CellSize));
				var lastRow = Math.Min(grid.Rows - 1, (int)Math.Floor((maxN - grid.OriginNorth) / grid.CellSize));
				var firstColumn = Math.Max(0, (int)Math.Ceiling((minE - grid.OriginEast) / grid.CellSize));
				var lastColumn = Math.Min(grid.Columns - 1, (int)Math.Floor((maxE - grid.OriginEast) / grid.CellSize));
				for (var r = firstRow; r <= lastRow; r++)
					for (var c = firstColumn; c <= lastColumn; c++)
					{
						var n = grid.OriginNorth + r * grid.CellSize; var e = grid.OriginEast + c * grid.CellSize;
						if (Inside(n, e)) Sample(n, e);
					}
			}
			for (var i = 0; i < outline.Length; i++)
			{
				var a = outline[i]; var delta = outline[(i + 1) % outline.Length] - a;
				var steps = Math.Max(1, (int)Math.Ceiling(delta.Magnitude / spacing));
				for (var j = 0; j <= steps; j++) Sample(a.North + delta.North * j / steps, a.East + delta.East * j / steps);
			}
			return minimum;
		}
	}
}
