using System;

namespace FerrySim.Console
{
	/// <summary>One refresh per rendered frame, with monotonic wall time and bounded focus priority.</summary>
	public sealed class ScreenScheduler
	{
		public const int Count = 5;
		private static readonly string[] Ids = { "radar", "plotter", "engine-port", "engine-starboard", "alarms" };
		private readonly bool[] _popOuts = new bool[Count];
		private readonly double[] _next = new double[Count];
		private bool _wheelhouse;
		private int _focus = -1, _lastFrame = int.MinValue;
		private double _lastTime = double.NaN;
		public static string Id(int index) => Ids[index];
		public static int Index(string id)
		{
			for (var i = 0; i < Count; i++) if (Ids[i] == id) return i;
			return -1;
		}
		public bool IsDemanded(int index) => _popOuts[index] || (_wheelhouse && index < 4);
		public void SetWheelhouseVisible(bool value) => _wheelhouse = value;
		public void SetPopOutShown(string id, bool value) { var i = Index(id); if (i >= 0) _popOuts[i] = value; }
		public void SetFocus(string id) => _focus = Index(id);
		public void Reset()
		{
			_lastFrame = int.MinValue; _lastTime = double.NaN;
			Array.Clear(_next, 0, _next.Length);
		}
		public int Next(double time, int frame)
		{
			if (frame == _lastFrame || double.IsNaN(time) || double.IsInfinity(time)) return -1;
			_lastFrame = frame;
			if (double.IsNaN(_lastTime) || time < _lastTime)
				for (var i = 0; i < Count; i++) _next[i] = time;
			_lastTime = time;
			var chosen = -1;
			for (var i = 0; i < Count; i++)
				if (IsDemanded(i) && time + 1e-9 >= _next[i] && (chosen < 0 || _next[i] < _next[chosen])) chosen = i;
			// Focus gets first claim unless another due screen has waited half a second. At low frame rates,
			// focus alone cannot consume every slot and starve another visible display.
			if (_focus >= 0 && IsDemanded(_focus) && time + 1e-9 >= _next[_focus] &&
				(chosen < 0 || time - _next[chosen] < .5)) chosen = _focus;
			if (chosen >= 0) _next[chosen] = time + (chosen == 0 ? .05 : .1);
			return chosen;
		}
	}
}
