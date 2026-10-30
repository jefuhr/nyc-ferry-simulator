using System;

namespace FerrySim.Harbor.Player
{
	/// <summary>Counts distinct swept spokes after opening, rather than assuming 2.5 s suffices on a slow player.</summary>
	public sealed class RadarCaptureReadiness
	{
		// An Astoria 0.75 NM picture must contain more than a handful of nearby echoes. This is a smoke floor,
		// not a feature-identification test: the real landmark assertions belong to harbor E2E.
		public const int MinimumEchoBins = 1000;
		private readonly bool[] _seen;
		private int _lastFrame = -1;
		public RadarCaptureReadiness(int spokes) { _seen = new bool[spokes]; }
		public int ObservedSpokes { get; private set; }
		public bool Complete => ObservedSpokes == _seen.Length;
		public void Observe(int frame, int firstSpoke, int count, bool cleared)
		{
			if (frame == _lastFrame) return;
			_lastFrame = frame;
			if (cleared) { Array.Clear(_seen, 0, _seen.Length); ObservedSpokes = 0; }
			for (var i = 0; i < count; i++)
			{
				var spoke = (firstSpoke + i) % _seen.Length;
				if (_seen[spoke]) continue;
				_seen[spoke] = true;
				ObservedSpokes++;
			}
		}
		public static bool ContentReady(bool fullSweep, int echoBins, int vertices, int targets) =>
			fullSweep && echoBins >= MinimumEchoBins && vertices > 0 && targets > 0;
	}
}
