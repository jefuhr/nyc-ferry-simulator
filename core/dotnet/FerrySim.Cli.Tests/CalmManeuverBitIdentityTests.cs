using System.IO;
using System.Security.Cryptography;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Cli.Tests
{
	// Failure mode guarded: the 12 calm manoeuvres (six manoeuvres, T-class and K-class, no other options) changing when
	// the start options, the current-field header lines and the transit were added: any byte of their CSVs, header
	// included. The SHA-256 values were recorded at 062bdf3, before those changes, with
	// `ferrysim maneuver <name> --vessel vessels/<t|k>-class.json --out <file>` on macOS arm64 (CoreCLR). A deliberate
	// change to the model or the CSV that moves them has to update them here and say why.
	[TestFixture]
	public sealed class CalmManeuverBitIdentityTests
	{
		[TestCase("t-class.json", "turning-circle", "90db201cde30896a9ab8ee4768bc4a8f59556d4a9cce47b8b6262b1d057e859a")]
		[TestCase("t-class.json", "zigzag", "d2f7e7abbc3ea87d67da09e3af9d10908cdf34b8826f7ea5098a950cef299927")]
		[TestCase("t-class.json", "crash-stop", "0307c205083f841e9bf6d94b099a5a971048bb7b43378261db6aad250844ba25")]
		[TestCase("t-class.json", "pivot", "c29ce58931963545d889ed8a3fb2e2c27f954cdf90d18df61405162d966c375d")]
		[TestCase("t-class.json", "acceleration", "5bcd7f9de83c9c6b983d122b93316fae45df6f395b8dfe8715de1cc601f6d669")]
		[TestCase("t-class.json", "stopping", "eb03bd50655a5377f3b96beb8b3b75f836ee0043bd1a3bb21b1cb1ae3a4acfd1")]
		[TestCase("k-class.json", "turning-circle", "ee6b5f364aab7de76521d22c6cceaf113e00eda40b01187a23814f867810bfd4")]
		[TestCase("k-class.json", "zigzag", "bd3687f58e69f361cb66df1499d86c7ff141b4cf9c2d8635d8c00648075d1458")]
		[TestCase("k-class.json", "crash-stop", "23df777c426a876b0f9dd835fc158b3c54407f2a7c5465393f851b795e9a4133")]
		[TestCase("k-class.json", "pivot", "929be4ce54f661b2f192f5d25b6127663b9d49f0a9a58cdc588e2bfb7e9c5839")]
		[TestCase("k-class.json", "acceleration", "3f468fe930e5ad04cfe86b6b56cf7b2daba2bbe70fa9d2dc69778d8be6034355")]
		[TestCase("k-class.json", "stopping", "4b91bd45c90b37de225ad20256fd05e7b494fe096e81cd10fac4a746d17ba397")]
		public void ACalmManeuverWithoutStartOptions_WritesTheSameBytesAsBefore(string vessel, string maneuver, string sha256)
		{
			using (var scratch = new TestSupport.ScratchDirectory())
			{
				var csv = scratch.File(maneuver + ".csv");
				var (code, _, stderr) = TestSupport.Run(new VesselDynamicsFactory(), "maneuver", maneuver, "--vessel", TestSupport.Vessel(vessel), "--out", csv);
				Assert.That(code, Is.EqualTo(0), stderr);

				using (var sha = SHA256.Create())
				{
					var hex = System.BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(csv))).Replace("-", string.Empty).ToLowerInvariant();
					Assert.That(hex, Is.EqualTo(sha256), $"{vessel} {maneuver}");
				}
			}
		}
	}
}
