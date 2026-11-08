using System;
using System.IO;
using System.Security.Cryptography;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Tests.Dynamics;
using NUnit.Framework;

namespace FerrySim.Core.Tests.Env
{
	/// <summary>Bit-level regression of wind, current, contacts and every state field against 1b64065.</summary>
	public class LandingDefaultReplayTests
	{
		[TestCase("t-class.json", "b3eb3f213febde6bbe731cd47d211267f33105991877070de6fe78f49050f3a1:8bc58daea11d9b43", false)]
		[TestCase("t-class.json", "b3eb3f213febde6bbe731cd47d211267f33105991877070de6fe78f49050f3a1:8bc58daea11d9b43", true)]
		[TestCase("k-class.json", "3ff7c1f695b1c461b313b3532f5f8177427e257f91c2fd44e5fff2886232b53a:b8275aa00658258c", false)]
		[TestCase("k-class.json", "3ff7c1f695b1c461b313b3532f5f8177427e257f91c2fd44e5fff2886232b53a:b8275aa00658258c", true)]
		public void DefaultReplayMatchesBase1b64065(string vesselFile, string expected, bool explicitDefaults)
		{
			var pack = PackFixtures.Pack(PackFixtures.Depth(50, PackFixtures.FlatGrid("flat", -1000, -1000, 1000, 3, 3, -50)),
				new[] { PackFixtures.SteadyCurrent("current", 0, 0, new HorizontalVector(.05, .02)) },
				new[] { PackFixtures.Gauge("level", 0, 0, t => 0) },
				new[] { PackFixtures.Wind("wind", 0, 0, t => new HorizontalVector(3, 4)) });
			var environment = explicitDefaults
				? new FerrySim.Core.Env.DataEnvironment(pack, PackFixtures.Epoch, new FerrySim.Core.Env.StationBlendCurrentField(pack.CurrentStations), new FerrySim.Core.Env.GustOptions())
				: new FerrySim.Core.Env.DataEnvironment(pack, PackFixtures.Epoch);
			var geometry = new SegmentListGeometry(new[] { new ContactSegment(new HorizontalVector(20, 30), new HorizontalVector(20, -30), ContactScenarios.EstimatedBargeFender, 7) });
			IWorldGeometry world = explicitDefaults ? new ContactSurfaceOverrides(geometry,
				new System.Collections.Generic.Dictionary<int, ContactSurface> { [7] = ContactScenarios.ContractTestFender }) : geometry;
			var sim = new VesselDynamicsSimulation(ManeuverRunner.Load(vesselFile), environment, world, VesselState.Planar(0, 0, 0, 0));
			using var bytes = new MemoryStream();
			using var writer = new BinaryWriter(bytes);
			ulong digest = 14695981039346656037UL;
			var touched = false;
			for (var step = 0; step < 12000; step++)
			{
				var inputs = step < 5000 ? new ControlInputs(.15, .15, 0) : step < 9000 ? new ControlInputs(.24, .15, .1) : ControlInputs.Neutral;
				writer.Write(inputs.PortLever); writer.Write(inputs.StarboardLever); writer.Write(inputs.Helm);
				sim.Step(inputs); touched |= sim.ContactCount > 0;
				var s = sim.State;
				foreach (var value in new[] { s.Time, s.North, s.East, s.Down, s.Roll, s.Pitch, s.Heading, s.Surge, s.Sway, s.Heave, s.RollRate, s.PitchRate, s.YawRate })
				{
					writer.Write(value);
					var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
					for (var shift = 0; shift < 64; shift += 8) digest = unchecked((digest ^ ((bits >> shift) & 255)) * 1099511628211UL);
				}
			}
			var output = bytes.ToArray();
			using var sha = SHA256.Create();
			var hash = BitConverter.ToString(sha.ComputeHash(output)).Replace("-", "").ToLowerInvariant();
			var folder = Path.Combine(RepoPaths.Root, "artifacts", "landing-behaviour"); Directory.CreateDirectory(folder);
			File.WriteAllBytes(Path.Combine(folder, vesselFile + ".replay.bin"), output);
			TestContext.Out.WriteLine($"{vesselFile}: sha256:{hash} fnv1a64:{digest:x16} bytes:{output.Length} contact:{touched}");
			Assert.That(touched, Is.True, "the replay must exercise fender contact");
			Assert.That(hash + ":" + digest.ToString("x16"), Is.EqualTo(expected));
		}
	}
}
