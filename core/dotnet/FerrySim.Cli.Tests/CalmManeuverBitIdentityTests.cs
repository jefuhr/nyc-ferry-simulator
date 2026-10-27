using System.IO;
using System.Security.Cryptography;
using FerrySim.Core.Dynamics;
using NUnit.Framework;

namespace FerrySim.Cli.Tests
{
	// Failure mode guarded: the 12 calm manoeuvres (six manoeuvres, T-class and K-class, no other options) changing when
	// the start options, the current-field header lines and the transit were added: any byte of their CSVs, header
	// included. Refreshed on 2026-10-26 for crew-reported 20 degree T/K stops, the crew-approved 4.5 L area/span/yaw-damping calibration and the vessel-derived zigzag divisor, with
	// `ferrysim maneuver <name> --vessel vessels/<t|k>-class.json --out <file>` on macOS arm64 (CoreCLR). A deliberate
	// change to the model or the CSV that moves them has to update them here and say why.
	[TestFixture]
	public sealed class CalmManeuverBitIdentityTests
	{
		[TestCase("t-class.json", "turning-circle", "c8b94339afd0c99386b0898743bca4da78a6fe6934548ecf071b12227f7d4a91")]
		[TestCase("t-class.json", "zigzag", "673dcfcccf8b8df04b63c27a3627e647ba6bfd9a5b612fc126c0692270bc1e07")]
		[TestCase("t-class.json", "crash-stop", "0307c205083f841e9bf6d94b099a5a971048bb7b43378261db6aad250844ba25")]
		[TestCase("t-class.json", "pivot", "c42fb06f96bc90169fb9f52e50742bb6ce16aa5f2dd1f11a2cddd4ff09e24d75")]
		[TestCase("t-class.json", "acceleration", "5bcd7f9de83c9c6b983d122b93316fae45df6f395b8dfe8715de1cc601f6d669")]
		[TestCase("t-class.json", "stopping", "eb03bd50655a5377f3b96beb8b3b75f836ee0043bd1a3bb21b1cb1ae3a4acfd1")]
		[TestCase("k-class.json", "turning-circle", "821a02f891f05e3aa56a17e5427fb391368319fa5eaf959f574ec6bc35a0c878")]
		[TestCase("k-class.json", "zigzag", "d75ddf3e627f0ba7112ff3e4b275102d3908ec48e480d16b5de6e438ff761733")]
		[TestCase("k-class.json", "crash-stop", "23df777c426a876b0f9dd835fc158b3c54407f2a7c5465393f851b795e9a4133")]
		[TestCase("k-class.json", "pivot", "4f457cdea2cff4b818cc03025008cf2f4ba86752ad79abad10d72340f623e1d5")]
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
