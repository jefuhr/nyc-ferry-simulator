namespace FerrySim.World
{
	/// <summary>
	/// Unity layer indices reserved for the simulator (coordinator decision, wave 2). Use these constants,
	/// never LayerMask.NameToLayer: index 9 briefly carried a second "Water" name, and a name lookup
	/// returns the first match.
	/// </summary>
	public static class WorldLayers
	{
		/// <summary>Unity's built-in Water layer: the water surface.</summary>
		public const int Water = 4;

		/// <summary>Fixed structures: piers, bulkheads, landing barges, bridges.</summary>
		public const int Structure = 8;

		/// <summary>Vessels.</summary>
		public const int Vessel = 10;
	}
}
