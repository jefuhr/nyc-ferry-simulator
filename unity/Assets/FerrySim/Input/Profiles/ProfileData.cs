using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Layouts;

namespace FerrySim.Controls.Profiles
{
	public enum DeviceProfileClass { Keyboard, Gamepad, ThrottleQuadrant, WheelOrKnob, GenericJoystick }
	public enum ProfileTarget { PortLever, StarboardLever, BothLevers, Helm }

	/// <summary>Raw, unprocessed Input System axis units. Ends can run in either direction.</summary>
	[Serializable]
	public sealed class AxisCalibration
	{
		public float astern = -1f, neutral, ahead = 1f, neutralBand = 0.05f;
		public AxisCalibration() { }
		public AxisCalibration(float astern, float neutral, float ahead, float band)
		{ this.astern = astern; this.neutral = neutral; this.ahead = ahead; neutralBand = band; }

		public bool IsValid => Finite(astern) && Finite(neutral) && Finite(ahead) && Finite(neutralBand)
			&& neutralBand >= 0f && ((double)neutral - astern) * ((double)ahead - neutral) > 0.0
			&& neutralBand < Math.Abs((double)neutral - astern) && neutralBand < Math.Abs((double)ahead - neutral);

		public float Apply(float raw, bool inverted = false)
		{
			if (!IsValid || !Finite(raw)) return 0f;
			var direction = ahead > astern ? 1.0 : -1.0;
			var offset = ((double)raw - neutral) * direction;
			var asternTravel = Math.Abs((double)astern - neutral);
			var aheadTravel = Math.Abs((double)ahead - neutral);
			// Endpoints take precedence over the rounding allowance at the neutral band.
			if (offset <= -asternTravel) return inverted ? 1f : -1f;
			if (offset >= aheadTravel) return inverted ? -1f : 1f;
			var tolerance = Math.Min(1e-7, Math.Min(asternTravel - neutralBand, aheadTravel - neutralBand) * 1e-6);
			if (Math.Abs(offset) <= neutralBand + tolerance) return 0f;
			var value = offset > 0.0
				? (offset - neutralBand) / (aheadTravel - neutralBand)
				: (offset + neutralBand) / (asternTravel - neutralBand);
			return (float)Math.Max(-1.0, Math.Min(1.0, inverted ? -value : value));
		}

		internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
	}

	/// <summary>A draft only: an interrupted calibration cannot replace a working saved assignment.</summary>
	public sealed class AxisCalibrationCapture
	{
		private readonly float[] _points = new float[3];
		public int Count { get; private set; }
		public void Capture(float value)
		{
			if (Count >= 3 || !AxisCalibration.Finite(value)) return;
			_points[Count++] = value;
		}
		public bool TryFinish(float band, out AxisCalibration result)
		{
			result = Count == 3 ? new AxisCalibration(_points[0], _points[1], _points[2], band) : null;
			if (result != null && result.IsValid) return true;
			result = null;
			return false;
		}
	}

	[Serializable]
	public sealed class AxisAssignment
	{
		/// <summary>Device-relative control path, e.g. stick/x, z, rz, slider or slider1.</summary>
		public string path;
		public ProfileTarget target;
		public bool inverted, relative;
		/// <summary>Normalized order per full-scale relative pulse, not a physical steering parameter.</summary>
		public float relativeStep = 0.1f;
		public AxisCalibration calibration = new AxisCalibration();

		public bool IsValid => !string.IsNullOrWhiteSpace(path) && !path.StartsWith("/")
			&& path.IndexOfAny(new[] { '*', '<', '>', '{', '}' }) < 0
			&& Enum.IsDefined(typeof(ProfileTarget), target) && calibration != null && calibration.IsValid
			&& AxisCalibration.Finite(relativeStep) && relativeStep > 0f && relativeStep <= 1f;
	}

	[Serializable]
	public sealed class DeviceProfile
	{
		public string identity;
		public DeviceProfileClass deviceClass;
		public List<AxisAssignment> axes = new List<AxisAssignment>();
		public bool IsValid
		{
			get
			{
				if (string.IsNullOrEmpty(identity) || axes == null || !Enum.IsDefined(typeof(DeviceProfileClass), deviceClass)) return false;
				var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				var targets = new HashSet<ProfileTarget>();
				foreach (var axis in axes)
				{
					if (axis == null || !axis.IsValid || !paths.Add(axis.path) || !targets.Add(axis.target)) return false;
				}
				return !targets.Contains(ProfileTarget.BothLevers)
					|| (!targets.Contains(ProfileTarget.PortLever) && !targets.Contains(ProfileTarget.StarboardLever));
			}
		}
		public DeviceProfile Copy() => JsonUtility.FromJson<DeviceProfile>(JsonUtility.ToJson(this));
	}

	public static class DeviceIdentity
	{
		// Length-prefixed fields avoid delimiter collisions. Layout/deviceId are deliberately absent:
		// deviceId changes on reconnect; serial distinguishes identical units when firmware exposes it.
		public static string Key(InputDeviceDescription description) => Part(description.interfaceName)
			+ Part(description.manufacturer) + Part(description.product) + Part(description.serial);
		public static string Key(InputDevice device) => Key(device.description);
		private static string Part(string value) => (value ?? "").Length.ToString(CultureInfo.InvariantCulture) + ":" + (value ?? "");
	}
}
