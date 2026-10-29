using FerrySim.Core.Env;
using FerrySim.Core.Serialization;
using FerrySim.Sim;

namespace FerrySim.Harbor
{
	/// <summary>
	/// Where the harbor catalog's start depth preview gets a day's water and bed: the harbor environment pack, loaded and
	/// verified. Tests give it a synthetic environment; at integration the harbor runtime's shared, verified depth set can
	/// stand behind it, so the catalog does not read the 5 m depth set a second time.
	/// </summary>
	public interface IStartDepthEnvironments
	{
		/// <summary>
		/// A fresh environment over <paramref name="day"/>'s pack, from the pack's local-midnight epoch, or null with the reason
		/// when the pack cannot be loaded or does not verify.
		/// </summary>
		DataEnvironment Get(EnvironmentPackDay day, out string problem);
	}

	/// <summary>Loads the day's pack through <see cref="EnvironmentPacks"/>, verifying every byte, as the slice's catalog does.</summary>
	public sealed class PackDepthEnvironments : IStartDepthEnvironments
	{
		private readonly EnvironmentPacks _packs;

		public PackDepthEnvironments(EnvironmentPacks packs)
		{
			_packs = packs ?? throw new System.ArgumentNullException(nameof(packs));
		}

		public DataEnvironment Get(EnvironmentPackDay day, out string problem)
		{
			try
			{
				var pack = _packs.Load(day.Name);
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
