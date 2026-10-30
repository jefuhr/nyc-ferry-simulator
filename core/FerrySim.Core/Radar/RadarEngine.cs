using System;

namespace FerrySim.Core.Radar
{
	/// <summary>Deterministic incremental PPI. Connection allocates all storage; each spoke is allocation-free.</summary>
	public sealed class RadarEngine
	{
		private readonly RadarScene _scene;
		private readonly float[] _echo;
		private readonly double[] _nearest;
		private readonly int[] _seen, _hits, _edgeSeen;
		private readonly double[] _clutterDecay = new double[1024];
		private readonly double[] _tops = new double[1024], _strength = new double[1024], _shadow = new double[1024];
		private int _generation, _hitCount;
		private long _cursor = -1;
		private double _lastTime = double.NaN;
		public RadarEngine(RadarScene scene, RadarConfiguration configuration = null)
		{
			_scene = scene ?? throw new ArgumentNullException(nameof(scene)); Configuration = configuration ?? new RadarConfiguration();
			_echo = new float[Configuration.Spokes * 1024]; _nearest = new double[scene.FeatureCount];
			_seen = new int[scene.FeatureCount]; _hits = new int[scene.FeatureCount]; _edgeSeen = new int[scene.EdgeCount]; Scale = RadarScale.At(RangeIndex); PrepareRange();
		}
		public RadarConfiguration Configuration { get; }
		public RadarScale Scale { get; private set; }
		public int RangeIndex { get; private set; } = 3;
		public double Gain { get; set; } = 1;
		public double Sea { get; set; } = .35;
		/// <summary>FTC is deliberately inert: there is no rain field and fog is not rain.</summary>
		public double Rain { get; set; }
		public int LastFirstSpoke { get; private set; }
		public int LastSpokeCount { get; private set; }
		public bool Cleared { get; private set; }
		public int PendingSpokes { get; private set; }
		/// <summary>Intersection attempts in the last spoke, including all three rays; evidence diagnostic.</summary>
		public int LastIntersectionTests { get; private set; }
		public float Echo(int spoke, int bin) => _echo[spoke * 1024 + bin];
		public void SetRange(int index)
		{
			if (index == RangeIndex) return; Scale = RadarScale.At(index); RangeIndex = index; PrepareRange(); Reset();
		}
		private void PrepareRange()
		{
			for (var b = 0; b < Scale.Bins; b++) { var range = Scale.Centre(b); _clutterDecay[b] = 1 / (1 + range * range / 40000); }
		}
		public void Reset() { Array.Clear(_echo, 0, _echo.Length); _cursor = -1; _lastTime = double.NaN; LastSpokeCount = 0; PendingSpokes = 0; Cleared = true; }
		/// <summary>At most the latest revolution is processed after a time jump; no unbounded catch-up.</summary>
		public void Advance(double time, RadarFrame frame, RadarTarget[] targets = null, int targetCount = 0)
		{
			if (double.IsNaN(time) || double.IsInfinity(time) || time < 0) throw new ArgumentOutOfRangeException(nameof(time));
			Cleared = false; LastSpokeCount = 0;
			if (time < _lastTime) Reset();
			var end = (long)Math.Floor(time * Configuration.RevolutionsPerSecond * Configuration.Spokes);
			if (_cursor < 0) { _cursor = end; _lastTime = time; Cleared = true; return; }
			var start = Math.Max(_cursor, end - Configuration.Spokes);
			var finish = Math.Min(end, start + Configuration.MaxSpokesPerFrame);
			LastFirstSpoke = (int)(start % Configuration.Spokes);
			for (var absolute = start; absolute < finish; absolute++) RenderSpoke((int)(absolute % Configuration.Spokes), absolute / Configuration.Spokes, frame, targets, targetCount);
			LastSpokeCount = (int)(finish - start); PendingSpokes = (int)(end - finish); _cursor = finish; _lastTime = time;
		}
		public void RenderSpoke(int spoke, long revolution, RadarFrame frame, RadarTarget[] targets = null, int targetCount = 0)
		{
			LastIntersectionTests = 0; var offset = spoke * 1024;
			Array.Clear(_echo, offset, Scale.Bins);
			var bearing = spoke * RadarMath.Tau / Configuration.Spokes;
			// Three deterministic rays approximate the estimated beam footprint; centre runs last for point shadow tests.
			Trace(bearing - Configuration.Beamwidth / 2, frame, offset);
			Trace(bearing + Configuration.Beamwidth / 2, frame, offset);
			Trace(bearing, frame, offset);
			for (var i = 0; i < _scene.Aids.Length; i++) Point(_scene.Aids[i], bearing, frame, offset, false);
			for (var i = 0; i < targetCount; i++) Point(targets[i], bearing, frame, offset, true);
			var windScale = Math.Min(1, Math.Max(0, frame.WindSpeed) / 15); var sea = Math.Max(0, Math.Min(1, Sea)); var gain = Math.Max(0, Gain);
			for (var b = 0; b < Scale.Bins; b++)
			{
				var noise = RadarMath.Noise(revolution, spoke, b);
				// Estimated clutter envelope and STC. Both fade smoothly over the first 600 m.
				var decay = _clutterDecay[b];
				var clutter = windScale * decay * noise * .6;
				var stc = 1 - sea * decay;
				_echo[offset + b] = (float)Math.Min(1, gain * ((_echo[offset + b] + clutter) * stc + noise * .025));
			}
		}
		private void Trace(double bearing, RadarFrame frame, int offset)
		{
			if (++_generation == int.MaxValue) { Array.Clear(_seen, 0, _seen.Length); Array.Clear(_edgeSeen, 0, _edgeSeen.Length); _generation = 1; }
			_hitCount = 0;
			var dn = Math.Cos(bearing); var de = Math.Sin(bearing);
			var n = RadarScene.Cell(frame.North); var e = RadarScene.Cell(frame.East);
			var sn = dn >= 0 ? 1 : -1; var se = de >= 0 ? 1 : -1;
			var nextN = Math.Abs(dn) < 1e-12 ? double.PositiveInfinity : ((n + (sn > 0 ? 1 : 0)) * RadarScene.CellSize - frame.North) / dn;
			var nextE = Math.Abs(de) < 1e-12 ? double.PositiveInfinity : ((e + (se > 0 ? 1 : 0)) * RadarScene.CellSize - frame.East) / de;
			var deltaN = RadarScene.CellSize / Math.Abs(dn); var deltaE = RadarScene.CellSize / Math.Abs(de);
			var distance = 0.0;
			while (distance <= Scale.RangeMetres)
			{
				if (_scene.Cells.TryGetValue(RadarScene.Key(n, e), out var cell)) for (var i = cell.Offset; i < cell.Offset + cell.Count; i++)
				{
					var edgeId = _scene.CellEdges[i];
					if (Configuration.DeduplicateEdges)
					{
						if (_edgeSeen[edgeId] == _generation) continue;
						_edgeSeen[edgeId] = _generation;
					}
					LastIntersectionTests++; var s = _scene.Segments[edgeId];
					// Subtract in double before geometry work; no float world coordinates enter the model.
					var an = s.N0 - frame.North; var ae = s.E0 - frame.East; var vn = s.N1 - s.N0; var ve = s.E1 - s.E0;
					var denominator = dn * ve - de * vn; if (Math.Abs(denominator) < 1e-12) continue;
					var t = (an * ve - ae * vn) / denominator; var u = (an * de - ae * dn) / denominator;
					if (t < 0 || t > Scale.RangeMetres || u < 0 || u > 1) continue;
					var f = _scene.EdgeFeatures[edgeId];
					if (_seen[f] != _generation) { _seen[f] = _generation; _nearest[f] = t; _hits[_hitCount++] = f; }
					else if (t < _nearest[f]) _nearest[f] = t;
				}
				if (nextN < nextE) { distance = nextN; nextN += deltaN; n += sn; }
				else { distance = nextE; nextE += deltaE; e += se; }
			}
			for (var b = 0; b < Scale.Bins; b++) { _tops[b] = double.NegativeInfinity; _strength[b] = 0; }
			var antenna = frame.Tide + frame.AntennaAboveWater;
			for (var i = 0; i < _hitCount; i++)
			{
				var f = _hits[i]; var feature = _scene.Features[f]; var range = _nearest[f]; var bin = Scale.Bin(range);
				if (feature.Land && range > RadarMath.HorizonMetres(frame.AntennaAboveWater, 0)) continue;
				if (feature.RoofNavd88.HasValue && range > RadarMath.HorizonMetres(frame.AntennaAboveWater, feature.RoofNavd88.Value - frame.Tide)) continue;
				var top = feature.Land ? double.NegativeInfinity : feature.RoofNavd88 ?? (feature.Bridge ? Configuration.UnknownBridgeRoof : Configuration.UnknownRoof);
				// Effective-earth curvature in the shadow elevation test, consistent with the horizon approximation.
				var slope = (top - antenna - range * range / (2 * 8494667.0)) / Math.Max(.01, range);
				if (slope > _tops[bin]) _tops[bin] = slope;
				_strength[bin] = 1;
			}
			var horizon = double.NegativeInfinity;
			for (var b = 0; b < Scale.Bins; b++)
			{
				_shadow[b] = horizon;
				if (_strength[b] > 0 && (_tops[b] >= horizon || double.IsNegativeInfinity(_tops[b]) && double.IsNegativeInfinity(horizon)))
				{
					Splat(offset, b, 1, Scale.PulseMetres);
					horizon = Math.Max(horizon, _tops[b]);
				}
			}
		}
		private void Point(RadarTarget target, double bearing, RadarFrame frame, int offset, bool vessel)
		{
			var n = target.North - frame.North; var e = target.East - frame.East; var range = RadarMath.Range(n, e);
			var bin = Scale.Bin(range); if (bin < 0 || range < .01 || range > RadarMath.HorizonMetres(frame.AntennaAboveWater, target.Height)) return;
			var angle = Math.Abs(RadarMath.Wrap(RadarMath.Bearing(n, e) - bearing + Math.PI) - Math.PI);
			var halfWidth = Configuration.Beamwidth / 2 + Math.Atan2(target.Length / 2, range);
			if (angle > halfWidth) return;
			var slope = (target.Height - frame.AntennaAboveWater - range * range / (2 * 8494667.0)) / range;
			if (slope < _shadow[bin]) return;
			// Estimated apparent response grows with projected length and height, capped at the reference 30 m × 8 m hull.
			var sizeResponse = vessel ? Math.Min(1, Math.Max(.25, Math.Sqrt(Math.Max(0, target.Length * target.Height) / 240))) : 1;
			Splat(offset, bin, target.Amplitude * sizeResponse * Math.Max(.2, 1 - angle / halfWidth), Scale.PulseMetres + target.Length);
		}
		private void Splat(int offset, int bin, double amplitude, double width)
		{
			var half = Math.Max(1, (int)Math.Ceiling(width * .5 * Scale.Bins / Scale.RangeMetres));
			for (var b = Math.Max(0, bin - half); b <= Math.Min(Scale.Bins - 1, bin + half); b++)
				_echo[offset + b] = Math.Max(_echo[offset + b], (float)(amplitude * (1 - .5 * Math.Abs(b - bin) / (half + 1))));
		}
	}
}
