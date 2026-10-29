namespace FerrySim.World.HarborTiles
{
	/// <summary>Render readiness only. Neither physics nor resident harbor queries depend on this interface.</summary>
	public interface IHarborViewLayer
	{
		string LayerId { get; }
		bool Required { get; }
		string Failure { get; }
		bool HasContent { get; }
		float LoadProgress { get; }
	}
}
