using System.Linq;
using FerrySim.Core.Instruments;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.WheelhouseInterior.Tests
{
	public class InteriorLampTests
	{
		internal static float[] Levels(InstrumentFixture f)
		{
			var lamps = f.Root.GetComponentsInChildren<Renderer>().Where(r => r.sharedMaterial.name == "LampsSurface").ToArray();
			Assert.That(lamps.Length, Is.EqualTo(1), "one lamp renderer");
			Assert.That(lamps[0].sharedMaterials.Length, Is.EqualTo(1));
			Assert.That(lamps[0].GetComponent<MeshFilter>().sharedMesh.subMeshCount, Is.EqualTo(1));
			Assert.That(lamps[0].sharedMaterial.shader.name, Is.EqualTo("FerrySim/WheelhouseLamps"));
			var block = new MaterialPropertyBlock(); lamps[0].GetPropertyBlock(block);
			return block.GetFloatArray("_LampLevels");
		}

		[TestCase("k-class")]
		[TestCase("t-class")]
		public void AlarmPhasesShareRhythm_PauseFreezes_UnmappedDark_LampTestLightsAll(string id)
		{
			using var f = new InstrumentFixture(id);
			foreach (var phase in new[] { AlarmPhase.Normal, AlarmPhase.Unacknowledged, AlarmPhase.Acknowledged, AlarmPhase.RingBack })
			{
				f.Alarms.Reset();
				if (phase != AlarmPhase.Normal) f.Alarms.Step(0, true, 0, 0);
				if (phase == AlarmPhase.Acknowledged) f.AcknowledgeAlarms();
				if (phase == AlarmPhase.RingBack) f.Alarms.Step(0, false, 0, 0);
				foreach (var time in new[] { 0.0, .49, .5, .99, 1, 1.49, 1.5, 1.99, 2 })
				{
					f.Time = time; f.View.Refresh();
					var levels = Levels(f);
					Assert.That(levels[0], Is.EqualTo(f.Alarms.LampOn(0, time) ? 1 : 0), phase + "/" + time);
					for (var i = 1; i < 14; i++) Assert.That(levels[i], Is.Zero, "unmapped/NaN " + i);
					f.View.Refresh(); Assert.That(Levels(f), Is.EqualTo(levels), "paused time");
				}
			}
			f.SetLampTest(true); f.View.Refresh();
			Assert.That(Levels(f).Take(14), Is.All.EqualTo(1));
			f.SetLampTest(false); f.Alarms.Reset(); f.View.Refresh();
			Assert.That(Levels(f).Take(14), Is.All.Zero);
		}

		[Test]
		public void MissingVesselAlarmLogsOnce_AndStaysDarkUntilLampTest()
		{
			using var f = new InstrumentFixture();
			f.AlarmName = "vessel.other";
			UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, "Wheelhouse SteeringAlarmLamp1: vessel does not define alarm steering.failure; lamp remains dark outside lamp test.");
			f.View.Bind(f);
			f.Alarms.Step(0, true, 0, 0);
			for (var i = 0; i < 10; i++) f.View.Refresh();
			Assert.That(Levels(f)[0], Is.Zero);
			UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
			f.SetLampTest(true); f.View.Refresh(); Assert.That(Levels(f)[0], Is.EqualTo(1));
		}

		[Test]
		public void NeutralLampsFollowOnlyNeutralShiftPhase()
		{
			using var f = new InstrumentFixture();
			for (var phase = 0; phase <= 6; phase++)
			{
				f.Values[(int)Channel.EnginePortShiftPhase] = phase;
				f.Values[(int)Channel.EngineStarboardShiftPhase] = 6 - phase;
				f.View.Refresh(); var levels = Levels(f);
				Assert.That(levels[10], Is.EqualTo(phase == 0 ? 1 : 0));
				Assert.That(levels[12], Is.EqualTo(phase == 6 ? 1 : 0));
			}
		}
	}
}
