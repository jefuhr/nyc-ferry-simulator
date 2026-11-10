using System;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Instruments;
using FerrySim.Core.Serialization;
using FerrySim.Sim;
using FerrySim.World;
using UnityEngine;

namespace FerrySim.WheelhouseInterior.Tests
{
	internal sealed class InstrumentFixture : IVesselTelemetrySource, IInstrumentSource, IInstrumentReadout, IDisposable
	{
		public readonly GameObject Root;
		public readonly WheelhouseControlsView View;
		public readonly double[] Values = new double[ChannelNames.Count];
		public readonly AlarmAnnunciator Alarms = new AlarmAnnunciator(new[] { new AlarmDefinition { Id = "steering.failure", Group = AlarmGroup.Steering } });
		public double Time;
		public bool Available = true;
		public long Run = 1;
		public string AlarmName = "steering.failure";
		public InstrumentFixture(string id = "k-class")
		{
			Parameters = VesselParametersJson.Load(Path.GetFullPath(Path.Combine(Application.dataPath, "../../vessels/" + id + ".json")));
			Root = UnityEngine.Object.Instantiate(Resources.Load<GameObject>("FerrySimVessels/" + id + "/Interior"));
			View = Root.GetComponent<WheelhouseControlsView>();
			for (var i = 0; i < Values.Length; i++) Values[i] = double.NaN;
			View.Bind(this);
		}
		public void Dispose() => UnityEngine.Object.DestroyImmediate(Root);
		public bool HasInstruments => Available;
		public IInstrumentReadout Instruments => this;
		public bool HasTelemetry => true;
		public VesselParameters Parameters { get; }
		public VesselState State => VesselState.Planar(Time, 0, 0, 0);
		public VesselTelemetry Telemetry => default;
		public ControlInputs Controls => new ControlInputs(-.7, .3, -.4);
		public DateTimeOffset ScenarioStartUtc => default;
		public long RunId => Run;
		public ContactLatch Contact => ContactLatch.None;
		public double Value(Channel channel) => Values[(int)channel];
		public int AlarmCount => Alarms.AlarmCount;
		public string AlarmId(int alarm) => AlarmName;
		public AlarmGroup Group(int alarm) => Alarms.Group(alarm);
		public AlarmPhase Phase(int alarm) => Alarms.Phase(alarm);
		public bool SounderOn(AlarmGroup group) => Alarms.SounderOn(group);
		public bool LampTest => Alarms.LampTest;
		public long FirstEventSequence => Alarms.FirstEventSequence;
		public long NextEventSequence => Alarms.NextEventSequence;
		public AlarmEvent EventAt(long sequence) => Alarms.EventAt(sequence);
		public void AcknowledgeAlarms() => Alarms.Acknowledge(AlarmGroup.Steering);
		public void SilenceAlarms() => Alarms.Silence(AlarmGroup.Steering);
		public void SetLampTest(bool on) => Alarms.SetLampTest(on);
	}
}
