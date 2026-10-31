using System;
using System.Collections;
using System.IO;
using FerrySim.Harbor.Player.Journeys;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.FrameStalls.Tests.PlayMode
{
	public class FrameProfilePlayTests
	{
		[UnityTest]
		public IEnumerator FrameRootMarkersKeepReportingWhileTheSamplerRunsInsideThem()
		{
			using var profile = new HarborFrameProfile(20);
			var directory = Path.Combine(Path.GetTempPath(), "ferrysim-roots-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			try
			{
				for (var frame = 0; frame < 5; frame++)
				{
					profile.Sample(frame, 2, 21);
					yield return null;
				}
				profile.Write(directory);
				var document = JObject.Parse(File.ReadAllText(Path.Combine(directory, "profile.json")));
				var id = -1;
				foreach (var item in document["markers"]) if ((string)item["name"] == "PlayerLoop") id = (int)item["id"];
				var observed = 0;
				foreach (var line in File.ReadAllLines(Path.Combine(directory, "profile-markers.csv")))
				{
					var cells = line.Split(',');
					if (cells[0] == "frame" || cells[1] != id.ToString()) continue;
					if (int.Parse(cells[0]) >= 2 && long.Parse(cells[2]) > 0 && long.Parse(cells[3]) > 0) observed++;
				}
				Assert.That(observed, Is.EqualTo(3), "resetting inside an open root discards its sample on every later frame");
				var first = File.ReadAllLines(Path.Combine(directory, "profile-frames.csv"))[1].Split(',');
				Assert.That(first[3], Is.EqualTo("-1"), "CPU time before the first sampling boundary is not part of its wall interval");
			}
			finally { Directory.Delete(directory, true); }
		}

		[UnityTest]
		public IEnumerator AnInactiveMarkerDoesNotReuseThePreviousFrameSample()
		{
			const string name = "FerrySim.FrameStalls.PositiveControl";
			var marker = new ProfilerMarker(name);
			using var profile = new HarborFrameProfile(20, new[] { name });
			var directory = Path.Combine(Path.GetTempPath(), "ferrysim-profile-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			try
			{
				profile.Sample(0, 2, 21);
				var beforeFrame = Time.frameCount;
				using (marker.Auto()) { }
				yield return null;
				Assert.That(Time.frameCount, Is.GreaterThan(beforeFrame), "a rendered frame boundary is required");
				profile.Sample(1, 2, 21);
				yield return null;
				profile.Sample(2, 2, 21);
				profile.Write(directory);
				var document = JObject.Parse(File.ReadAllText(Path.Combine(directory, "profile.json")));
				var id = -1;
				foreach (var item in document["markers"]) if ((string)item["name"] == name) id = (int)item["id"];
				Assert.That(id, Is.GreaterThanOrEqualTo(0));
				var firstCount = 0L; var secondCount = 0L;
				foreach (var line in File.ReadAllLines(Path.Combine(directory, "profile-markers.csv")))
				{
					var cells = line.Split(',');
					if (cells[0] == "frame" || cells[1] != id.ToString()) continue;
					if (cells[0] == "1") firstCount += long.Parse(cells[3]);
					if (cells[0] == "2") secondCount += long.Parse(cells[3]);
				}
				Assert.That(firstCount, Is.EqualTo(1), "one deliberate marker sample in the preceding frame");
				Assert.That(secondCount, Is.Zero, "no stale last value on the inactive frame");
			}
			finally { Directory.Delete(directory, true); }
		}
	}
}
