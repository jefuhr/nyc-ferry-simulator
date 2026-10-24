using System;
using System.IO;
using NUnit.Framework;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Layouts;
using FerrySim.Controls.Profiles;

namespace FerrySim.Controls.ProfileTests
{
	public class ProfileTests : InputTestFixture
	{
		private string _path;
		public override void Setup()
		{
			base.Setup();
			_path = Path.Combine(Path.GetTempPath(), "ferrysim-profile-" + Guid.NewGuid() + ".json");
		}
		public override void TearDown()
		{
			if (File.Exists(_path)) File.Delete(_path);
			base.TearDown();
		}

		[TestCase(-0.8f, -1f)]
		[TestCase(0.2f, 0f)]
		[TestCase(0.16f, 0f)]
		[TestCase(0.24f, 0f)]
		[TestCase(0.67f, 0.5f)]
		[TestCase(-0.32f, -0.5f)]
		[TestCase(1.1f, 1f)]
		public void OffCentreDetent_MapsExactly(float raw, float expected)
		{
			var axis = new AxisCalibration(-0.8f, 0.2f, 1.1f, 0.04f);
			Assert.That(axis.Apply(raw), Is.EqualTo(expected).Within(1e-6));
		}

		[Test]
		public void InvertedAxis_MapsBothEndsAndNeutral()
		{
			var axis = new AxisCalibration(1f, 0.3f, -1f, 0.05f);
			Assert.That(axis.Apply(1f), Is.EqualTo(-1f));
			Assert.That(axis.Apply(-1f), Is.EqualTo(1f));
			Assert.That(axis.Apply(0.32f), Is.EqualTo(0f));
			Assert.That(axis.Apply(-1f, true), Is.EqualTo(-1f));
		}

		[TestCase(0f, 0f, 1f, 0.01f)]
		[TestCase(-1f, 2f, 1f, 0.01f)]
		[TestCase(-1f, 0f, 1f, 1f)]
		[TestCase(-1f, 0f, float.NaN, 0f)]
		public void InvalidCalibration_IsRejected(float astern, float neutral, float ahead, float band)
		{
			Assert.That(new AxisCalibration(astern, neutral, ahead, band).IsValid, Is.False);
		}

		[Test]
		public void UnfinishedCapture_CannotReplaceSavedCalibration()
		{
			var capture = new AxisCalibrationCapture();
			capture.Capture(-0.8f);
			capture.Capture(0.2f);
			Assert.That(capture.TryFinish(0.04f, out _), Is.False);
			capture.Capture(1.1f);
			Assert.That(capture.TryFinish(0.04f, out var result), Is.True);
			Assert.That(result.Apply(0.2f), Is.Zero);
		}

		[Test]
		public void Identity_SeparatesSerialsAndEscapesFields()
		{
			var a = Description("A");
			var b = Description("B");
			Assert.That(DeviceIdentity.Key(a), Is.Not.EqualTo(DeviceIdentity.Key(b)));
			var collision = a;
			collision.manufacturer = "Test|Quadrant";
			collision.product = "";
			Assert.That(DeviceIdentity.Key(a), Is.Not.EqualTo(DeviceIdentity.Key(collision)));
		}

		[Test]
		public void Persistence_RoundTripAndReset_KeepSerialsSeparate()
		{
			var store = new DeviceProfileStore(_path);
			var a = Profile("A");
			var b = Profile("B");
			b.axes[0].inverted = true;
			store.Set(a);
			store.Set(b);
			Assert.That(store.Save(), Is.True, store.LastError);
			var loaded = new DeviceProfileStore(_path);
			Assert.That(loaded.Load(), Is.True, loaded.LastError);
			Assert.That(loaded.Find(a.identity).axes[0].calibration.Apply(0.2f), Is.Zero);
			Assert.That(loaded.Find(b.identity).axes[0].inverted, Is.True);
			loaded.Reset(a.identity);
			Assert.That(loaded.Save(), Is.True);
			Assert.That(loaded.Load(), Is.True);
			Assert.That(loaded.Find(a.identity), Is.Null);
			Assert.That(loaded.Find(b.identity), Is.Not.Null);
		}

		[TestCase("{oops")]
		[TestCase("{\"version\":0,\"devices\":[]}")]
		[TestCase("{\"version\":999,\"devices\":[]}")]
		[TestCase("{}")]
		public void CorruptOrUnsupportedFile_IsReportedAndIgnored(string json)
		{
			File.WriteAllText(_path, json);
			var store = new DeviceProfileStore(_path);
			Assert.That(store.Load(), Is.False);
			Assert.That(store.LastError, Is.Not.Empty);
			Assert.That(store.Find(Profile("A").identity), Is.Null);
			Assert.That(File.ReadAllText(_path), Is.EqualTo(json), "loading must not overwrite the evidence");
		}

		[Test]
		public void Store_RejectsIncompleteAxisAndOwnsACopy()
		{
			var store = new DeviceProfileStore(_path);
			var profile = Profile("A");
			store.Set(profile);
			profile.axes.Clear();
			Assert.That(store.Find(profile.identity).axes.Count, Is.EqualTo(1));
			var bad = Profile("B");
			bad.axes[0].calibration = new AxisCalibration(0, 0, 0, 0);
			Assert.Throws<ArgumentException>(() => store.Set(bad));
		}

		[TestCase(-5e-8f, 0f, 5e-8f)]
		[TestCase(-float.MaxValue, 1e38f, float.MaxValue)]
		[TestCase(-1f, 0f, 1f)]
		[TestCase(5e-8f, 0f, -5e-8f)]
		public void AcceptedExtremeCaptures_PreserveEndpointsAndFiniteInterior(float astern, float neutral, float ahead)
		{
			var axis = new AxisCalibration(astern, neutral, ahead, 0f);
			Assert.That(axis.IsValid, Is.True);
			Assert.That(axis.Apply(astern), Is.EqualTo(-1f));
			Assert.That(axis.Apply(ahead), Is.EqualTo(1f));
			Assert.That(axis.Apply(neutral), Is.Zero);
			foreach (var raw in new[] { astern, neutral, ahead, (float)(((double)astern + neutral) / 2), (float)(((double)ahead + neutral) / 2) })
				Assert.That(axis.Apply(raw), Is.InRange(-1f, 1f));
		}

		public static InputDeviceDescription Description(string serial) => new InputDeviceDescription
		{
			interfaceName = "HID", manufacturer = "Test", product = "Quadrant", serial = serial
		};
		private static DeviceProfile Profile(string serial) => new DeviceProfile
		{
			identity = DeviceIdentity.Key(Description(serial)), deviceClass = DeviceProfileClass.ThrottleQuadrant,
			axes = new System.Collections.Generic.List<AxisAssignment>
			{
				new AxisAssignment { path = "slider", target = ProfileTarget.PortLever,
					calibration = new AxisCalibration(-0.8f, 0.2f, 1.1f, 0.04f) }
			}
		};
	}
}
