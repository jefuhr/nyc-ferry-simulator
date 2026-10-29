using System;
using FerrySim.World.HarborTiles;

namespace FerrySim.Harbor
{
	public enum HarborViewState { Waiting, Ready, Failed }

	/// <summary>One start's render gate. Once ready, subsequent tile loading never holds a sailing vessel.</summary>
	public sealed class HarborInitialView
	{
		private readonly IHarborViewLayer[] _layers;
		private readonly int _stableFrames;
		private readonly double _timeout;
		private double _started;
		private int _completeFrames;

		public HarborInitialView(IHarborViewLayer[] layers, int stableFrames = 10, double timeoutSeconds = 60)
		{
			if (layers == null || layers.Length == 0) throw new ArgumentException("The harbor needs its required view layers.", nameof(layers));
			if (stableFrames < 1 || timeoutSeconds <= 0 || double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds))
				throw new ArgumentOutOfRangeException(nameof(stableFrames));
			_layers = (IHarborViewLayer[])layers.Clone();
			for (var i = 0; i < _layers.Length; i++)
				if (_layers[i] == null) throw new ArgumentException("A harbor view layer is missing.", nameof(layers));
			_stableFrames = stableFrames;
			_timeout = timeoutSeconds;
		}
		public HarborViewState State { get; private set; }
		public string Problem { get; private set; }
		public void Begin(double now)
		{
			_started = now;
			_completeFrames = 0;
			Problem = null;
			State = HarborViewState.Waiting;
		}

		public void Poll(double now)
		{
			if (State != HarborViewState.Waiting) return;
			string waitingFor = null;
			for (var i = 0; i < _layers.Length; i++)
			{
				var layer = _layers[i];
				if (!layer.Required) continue;
				if (!string.IsNullOrEmpty(layer.Failure))
				{
					Fail(layer.LayerId + ": " + layer.Failure);
					return;
				}
				// NaN is incomplete too. Cesium may report 100 before the first content object exists.
				if (!layer.HasContent || !(layer.LoadProgress >= 100f)) waitingFor = layer.LayerId;
			}
			_completeFrames = waitingFor == null ? _completeFrames + 1 : 0;
			if (_completeFrames >= _stableFrames) State = HarborViewState.Ready;
			else if (now - _started >= _timeout) Fail("The initial harbor view timed out waiting for " + (waitingFor ?? "stable terrain and skyline") + ".");
		}

		private void Fail(string reason)
		{
			Problem = reason;
			State = HarborViewState.Failed;
		}
	}
}
