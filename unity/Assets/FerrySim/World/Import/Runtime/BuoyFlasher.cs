using UnityEngine;

namespace FerrySim.World.Import
{
	/// <summary>The bridge may supply scenario time through SetSimulationTime; standalone previews use elapsed play time.</summary>
	public sealed class BuoyFlasher : MonoBehaviour
	{
		public LightCharacteristic Characteristic;
		public Light Lamp;
		public Renderer Lens;
		private bool _externalTime;
		private double _time;
		public void SetSimulationTime(double seconds) { _externalTime = true; _time = seconds; Apply(seconds); }
		private void Update() { Apply(_externalTime ? _time : Time.timeAsDouble); }
		private void Apply(double seconds)
		{
			var lit = Characteristic != null && Characteristic.IsLit(seconds);
			if (Lamp != null) Lamp.enabled = lit;
			if (Lens != null) Lens.enabled = lit;
		}
	}
}
