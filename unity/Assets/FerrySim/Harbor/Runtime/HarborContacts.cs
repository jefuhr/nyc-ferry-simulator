using System.IO;
using FerrySim.Slice;

namespace FerrySim.Harbor
{
	public static class HarborContacts
	{
		/// <summary>Uses the existing contact parser and surface policy, including its source orientation and berth gates.</summary>
		public static LandingGeometry Load(string contactsPath, string surfaceJson, string surfacePath)
		{
			if (!File.Exists(contactsPath)) throw new FileNotFoundException("Missing harbor contacts: " + contactsPath, contactsPath);
			return LandingGeometry.Parse(File.ReadAllText(contactsPath), contactsPath, LandingSurfaceTable.Parse(surfaceJson, surfacePath));
		}
	}
}
