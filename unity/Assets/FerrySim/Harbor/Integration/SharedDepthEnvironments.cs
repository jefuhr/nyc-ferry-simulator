using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using FerrySim.Sim;

namespace FerrySim.Harbor
{
	/// <summary>
	/// The catalog's depth preview over the runner's resident verified depth set: a day is loaded through
	/// <see cref="EnvironmentPacks.LoadHarborDay"/>, so the 74 MB depth set is read and hashed once for the preview and
	/// the atomic start alike (decision 0005, Resident data).
	/// </summary>
	public sealed class SharedDepthEnvironments : IStartDepthEnvironments
	{
		private readonly EnvironmentPacks _packs;

		public SharedDepthEnvironments(EnvironmentPacks packs)
		{
			_packs = packs ?? throw new System.ArgumentNullException(nameof(packs));
		}

		public DataEnvironment Get(EnvironmentPackDay day, out string problem)
		{
			try
			{
				var pack = _packs.LoadHarborDay(day.Name);
				problem = null;
				return new DataEnvironment(pack, pack.EpochUtc);
			}
			catch (System.Exception error) when (error is System.IO.IOException || error is System.UnauthorizedAccessException || error is EnvironmentPackException ||
				error is System.FormatException || error is System.ArgumentException)
			{
				problem = error.Message;
				return null;
			}
		}
	}
}
