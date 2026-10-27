using System;
using FerrySim.Core.Contracts;

namespace FerrySim.World
{
	/// <summary>Render-only approach hysteresis. Distances are from the bow to a water-side contact face ahead.</summary>
	public sealed class BowLookState
	{
		public const double EnterDistance = 20.0;
		public const double ExitDistance = 30.0;
		public const double EnterSpeed = 1.0;
		public const double ExitSpeed = 1.5;
		private bool _approach;
		private bool _handOverride;
		private bool _automatic;
		public bool Manual { get; private set; }
		public bool ShowBow => Manual || (_automatic && _approach && !_handOverride);

		public void UpdateApproach(double distance, double speed, bool automatic)
		{
			_automatic = automatic;
			if (_approach && (distance > ExitDistance || speed > ExitSpeed))
			{
				_approach = false;
				_handOverride = false;
			}
			else if (!_approach && distance <= EnterDistance && speed <= EnterSpeed)
			{
				_approach = true;
				_handOverride = false;
			}
		}

		public void ToggleManual()
		{
			// B toggles the visible preset, including a view selected by automatic approach.
			Manual = !ShowBow;
			if (!Manual) _handOverride = true;
		}

		public void HandLook() { Manual = false; _handOverride = true; }

		public void Reset() { Manual = false; _approach = false; _handOverride = false; }

		public static double FaceDistance(IWorldGeometry geometry, ContactSegment[] buffer,
			double north, double east, double heading, double bowForward)
		{
			var forward = new HorizontalVector(Math.Cos(heading), Math.Sin(heading));
			var bow = new HorizontalVector(north, east) + forward * bowForward;
			var count = geometry.QuerySegments(bow.North, bow.East, ExitDistance + 1, buffer);
			var nearest = double.PositiveInfinity;
			for (var i = 0; i < count; i++)
			{
				var face = buffer[i];
				var normal = face.WaterNormal;
				// Do not tilt toward a wall alongside or behind the bow, or a solid-side face.
				if (normal.North * forward.North + normal.East * forward.East > -.5) continue;
				var offset = bow - face.Start;
				if (offset.North * normal.North + offset.East * normal.East < 0) continue;
				nearest = Math.Min(nearest, face.DistanceTo(bow.North, bow.East));
			}
			return nearest;
		}
	}
}
