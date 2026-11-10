using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Sim;
using Newtonsoft.Json.Linq;

namespace FerrySim.Harbor.Player.Journeys
{
	/// <summary>Test-only estimates and literal source geometry; never a product navigation aid.</summary>
	public sealed class HarborJourney
	{
		public string Name, Departure, Arrival, Date;
		public bool ArrivalBlocked, HoldOnly;
		public double ForceLimit;
		public int Minute;
		public HorizontalVector[] Points;
		public string[] SpanIds;
		public HorizontalVector[][] SpanRings;
		public HorizontalVector FaceMiddle, FaceNormal;
		public int[] FaceIds;
		public bool ApproachOnly { get; private set; }
		public HorizontalVector StartPosition { get; private set; }
		public double StartHeading { get; private set; }
		public HorizontalVector[] ApproachCorridor { get; private set; }

		/// <summary>Only segment identities on the intended face, under positive normal load, can latch a hold.</summary>
		public bool IntendedFaceContact(VesselDynamicsSimulation simulation)
		{
			for (var i = 0; i < simulation.ContactCount; i++)
			{
				var contact = simulation.GetContact(i);
				if (contact.NormalForce > 0 && Array.IndexOf(FaceIds, contact.SegmentId) >= 0) return true;
			}
			return false;
		}

		public void SetApproachStart(JToken start)
		{
			if (start == null) return;
			if (!(start is JObject)) throw new FormatException("Approach start must be an object.");
			if ((string)start["mode"] != "approach-only") throw new FormatException("Unknown journey start mode.");
			StartPosition = FiniteVector(start["position_ne_m"]);
			var heading = start["heading_rad"];
			if (heading == null || (heading.Type != JTokenType.Integer && heading.Type != JTokenType.Float) ||
				double.IsNaN((double)heading) || double.IsInfinity((double)heading)) throw new FormatException("Approach heading must be finite radians.");
			StartHeading = (double)heading;
			if (!(start["corridor_ne_m"] is JArray ring) || ring.Count < 4) throw new FormatException("An approach needs a closed convex corridor.");
			ApproachCorridor = new HorizontalVector[ring.Count];
			for (var i = 0; i < ring.Count; i++) ApproachCorridor[i] = FiniteVector(ring[i]);
			if ((ApproachCorridor[0] - ApproachCorridor[ring.Count - 1]).Magnitude != 0) throw new FormatException("Approach corridor must be closed.");
			var area = 0.0;
			for (var i = 0; i < ring.Count - 1; i++)
			{
				var a = ApproachCorridor[i]; var b = ApproachCorridor[i + 1];
				if ((b - a).Magnitude == 0) throw new FormatException("Approach corridor has an empty edge.");
				area += a.North * b.East - b.North * a.East;
			}
			if (area == 0 || double.IsInfinity(area) || double.IsNaN(area)) throw new FormatException("Approach corridor has no finite area.");
			for (var i = 0; i < ring.Count - 1; i++)
				for (var j = 0; j < ring.Count - 1; j++)
					if (Cross(ApproachCorridor[i + 1] - ApproachCorridor[i], ApproachCorridor[j] - ApproachCorridor[i]) * Math.Sign(area) < -1e-9)
						throw new FormatException("Approach corridor must be convex without crossing edges.");
			if (Points == null || Points.Length == 0) throw new FormatException("An approach needs at least one waypoint.");
			if (!InApproachCorridor(StartPosition)) throw new FormatException("Approach start is outside its corridor.");
			foreach (var point in Points)
				if (!Finite(point.North) || !Finite(point.East) || !InApproachCorridor(point)) throw new FormatException("Approach waypoint is outside its corridor.");
			ApproachOnly = true;
		}

		public bool InApproachCorridor(HorizontalVector point)
		{
			var positive = false; var negative = false;
			for (var i = 0; i < ApproachCorridor.Length - 1; i++)
			{
				var cross = Cross(ApproachCorridor[i + 1] - ApproachCorridor[i], point - ApproachCorridor[i]);
				positive |= cross > 1e-9; negative |= cross < -1e-9;
			}
			return !(positive && negative);
		}

		private static double Cross(HorizontalVector a, HorizontalVector b) => a.North * b.East - a.East * b.North;
		private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
		private static HorizontalVector FiniteVector(JToken token)
		{
			if (!(token is JArray a) || a.Count != 2 || a[0].Type != JTokenType.Integer && a[0].Type != JTokenType.Float ||
				a[1].Type != JTokenType.Integer && a[1].Type != JTokenType.Float) throw new FormatException("Approach positions need two finite SI coordinates.");
			var point = Vector(token);
			if (!Finite(point.North) || !Finite(point.East)) throw new FormatException("Approach positions need two finite SI coordinates.");
			return point;
		}
		public static HarborJourney Parse(JToken token)
		{
			var result = new HarborJourney { Name = (string)token["name"], Departure = (string)token["departure_stop"], Arrival = (string)token["arrival_stop"],
				Date = (string)token["date"], Minute = (int)token["minute"], ArrivalBlocked = (bool)token["arrival_blocked"] };
			result.HoldOnly = result.ArrivalBlocked || (bool?)token["arrival_hold"] == true;
			result.ForceLimit = (double?)token["estimated_force_limit_n"] ?? double.PositiveInfinity;
			var points = (JArray)token["points"];
			result.Points = new HorizontalVector[points.Count];
			for (var i = 0; i < points.Count; i++) result.Points[i] = Vector(points[i]);
			var spans = (JArray)token["required_spans"];
			result.SpanIds = new string[spans.Count]; result.SpanRings = new HorizontalVector[spans.Count][];
			for (var i = 0; i < spans.Count; i++)
			{
				result.SpanIds[i] = (string)spans[i]["id"];
				var ring = (JArray)spans[i]["geometry_ne_m"]["coordinates"][0];
				result.SpanRings[i] = new HorizontalVector[ring.Count];
				for (var j = 0; j < ring.Count; j++) result.SpanRings[i][j] = Vector(ring[j]);
			}
			if (!result.HoldOnly)
			{
				result.FaceMiddle = Vector(token["arrival_face"]["middle"]); result.FaceNormal = Vector(token["arrival_face"]["normal"]);
				result.FaceIds = token["arrival_face"]["segment_ids"].ToObject<int[]>();
			}
			else result.FaceIds = Array.Empty<int>();
			result.SetApproachStart(token["start"]);
			return result;
		}
		private static HorizontalVector Vector(JToken p) => new HorizontalVector((double)p[0], (double)p[1]);
		public static bool Inside(HorizontalVector point, HorizontalVector[] ring)
		{
			var inside = false;
			for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
			{
				var a = ring[i]; var b = ring[j];
				if ((a.North > point.North) != (b.North > point.North) && point.East < (b.East - a.East) * (point.North - a.North) / (b.North - a.North) + a.East) inside = !inside;
			}
			return inside;
		}
	}

	/// <summary>Scripted helm authoring, sampled every 0.1 s and applied on the actual 100 Hz input clock.</summary>
	public sealed class HarborJourneyControls : IControlInputSource
	{
		private readonly HarborJourney _route;
		private readonly Func<VesselState> _state;
		private readonly Func<bool> _contact;
		private readonly double _length;
		private readonly VesselState _initial;
		private ControlInputs _controls;
		private long _contactStep = -1, _holdStep = -1;
		private HorizontalVector _legStart;
		public int Phase { get; private set; } = -1;
		public bool Complete { get; private set; }
		public HarborJourneyControls(HarborJourney route, Func<VesselState> state, Func<bool> contact, double length)
		{
			_route = route; _state = state; _contact = contact; _length = length; _initial = state(); _legStart = _initial.Position;
			if (route.ApproachOnly)
			{
				if ((_initial.Position - route.StartPosition).Magnitude > 1e-6 || Math.Abs(Angles.WrapPi(_initial.Heading - route.StartHeading)) > 1e-9)
					throw new ArgumentException("The actual approach start must match the declared position and heading.");
				Phase = 0;
			}
		}
		public ControlInputs ControlsForStep(long step)
		{
			if (Complete) return ControlInputs.Neutral;
			var s = _state();
			if (_route.ApproachOnly && Phase < _route.Points.Length && !_route.InApproachCorridor(s.Position))
				throw new InvalidOperationException("The approach left its verified corridor.");
			if (_contact() && _contactStep < 0) _contactStep = step;
			if (step % 10 != 0) return _controls;
			if (_contactStep >= 0)
			{
				Complete = step >= _contactStep + 6000;
				var heading = Math.Atan2(-_route.FaceNormal.East, -_route.FaceNormal.North);
				var error = Angles.WrapPi(heading - s.Heading);
				var twist = Clamp(.8 * error - 7 * s.YawRate, -.1, .1);
				return _controls = new ControlInputs(Round(.15 + twist), Round(.15 - twist), Round(2 * error - 5 * s.YawRate));
			}
			if (Phase < 0)
			{
				var backed = -(s.North - _initial.North) * Math.Cos(_initial.Heading) - (s.East - _initial.East) * Math.Sin(_initial.Heading);
				_controls = Navigate(s, _initial.Heading, -1.5);
				if (backed >= 55) { Phase = 0; _legStart = s.Position; }
			}
			else if (Phase < _route.Points.Length)
			{
				var end = _route.Points[Phase];
				var delta = end - s.Position;
				var leg = end - _legStart;
				var length = Math.Max(1, leg.Magnitude);
				var along = Dot(s.Position - _legStart, leg) / length;
				var cross = Math.Abs((s.North - _legStart.North) * leg.East - (s.East - _legStart.East) * leg.North) / length;
				var last = Phase == _route.Points.Length - 1;
				var look = Math.Max(30, Math.Min(110, Math.Abs(s.Surge) * 7));
				var aim = _legStart + leg * (Math.Min(length, Math.Max(0, along) + look) / length);
				var heading = Math.Atan2(aim.East - s.East, aim.North - s.North);
				var speed = 12.8611111111111;
				if (last) speed = Math.Min(speed, Math.Sqrt(.18 * delta.Magnitude));
				if (!last && delta.Magnitude < 180)
				{
					var next = _route.Points[Phase + 1] - end;
					var turn = Math.Abs(Angles.WrapPi(Math.Atan2(next.East, next.North) - Math.Atan2(leg.East, leg.North)));
					speed = Math.Min(speed, Math.Max(2, 12.86 - turn * 9));
				}
				if (Math.Abs(Angles.WrapPi(heading - s.Heading)) > .4) speed = Math.Min(speed, 1.0);
				_controls = Navigate(s, heading, speed);
				if ((delta.Magnitude < (last ? 12 : 25) || (!last && along >= length && cross < 40)) && (!last || Math.Abs(s.Surge) < 1.2))
				{ Phase++; _legStart = end; }
			}
			else if (_route.HoldOnly)
			{
				var delta = _route.Points[_route.Points.Length - 1] - s.Position;
				var speed = Math.Min(.5, delta.Magnitude * .05);
				_controls = Navigate(s, Math.Atan2(delta.East, delta.North), speed);
				if (delta.Magnitude < 25 && s.GroundVelocity.Magnitude < .6)
				{ if (_holdStep < 0) _holdStep = step; Complete = step >= _holdStep + 3000; }
				else _holdStep = -1;
			}
			else
			{
				var fromFace = s.Position - _route.FaceMiddle;
				var distance = Dot(fromFace, _route.FaceNormal) - _length * .5;
				var cross = fromFace.North * -_route.FaceNormal.East + fromFace.East * _route.FaceNormal.North;
				var heading = Math.Atan2(-_route.FaceNormal.East, -_route.FaceNormal.North) + Math.Atan2(cross, Math.Max(25, distance));
				var speed = distance > 25 ? 1.3 : .42;
				if (Math.Abs(Angles.WrapPi(heading - s.Heading)) > .25) speed = 0;
				_controls = Navigate(s, heading, speed);
			}
			return _controls;
		}
		private ControlInputs Navigate(in VesselState s, double heading, double speed)
		{
			var error = Angles.WrapPi(heading - s.Heading);
			var common = Clamp(.4 * (speed - s.Surge) + .02 * speed, -.5, .95);
			var twist = Clamp(1.3 * error - 7 * s.YawRate, -.4, .4);
			return new ControlInputs(Round(common + twist), Round(common - twist), Round(2 * error - 5 * s.YawRate));
		}
		private static double Dot(HorizontalVector a, HorizontalVector b) => a.North * b.North + a.East * b.East;
		private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));
		private static double Round(double v) => Math.Round(Clamp(v, -1, 1), 2, MidpointRounding.AwayFromZero);
	}

	/// <summary>Every physics step, including between rendered frames; contact IDs cannot be inferred from a screenshot.</summary>
	public sealed class HarborJourneyObserver : IStepObserver
	{
		private readonly HarborJourney _route;
		private readonly VesselDynamicsSimulation _simulation;
		public readonly JourneyFailures Failures = new JourneyFailures();
		public readonly List<string> Crossings;
		private readonly bool[] _crossed;
		private double _beforeSpeed;
		private ControlInputs _beforeControls;
		private readonly bool[] _holdBad = new bool[1000];
		private readonly HoldSample[] _holdSamples = new HoldSample[1000];
		private int _holdCursor, _holdCount, _holdBadCount;
		public double PeakForce { get; private set; }
		public double PeakCompression { get; private set; }
		public bool BottomedOut { get; private set; }
		public bool FinalHoldOkay => _holdCount == 1000 && _holdBadCount == 0;
		public double FirstContactSpeed { get; private set; } = -1;
		public int FirstContactId { get; private set; } = -1;
		public long GroundedSteps { get; private set; }
		public double MinimumUnderKeel { get; private set; } = double.PositiveInfinity;
		public HarborJourneyObserver(HarborJourney route, VesselDynamicsSimulation simulation) { _route = route; _simulation = simulation; _crossed = new bool[route.SpanIds.Length]; Crossings = new List<string>(route.SpanIds.Length); }
		public string LandingVerdict(in VesselState state)
		{
			if (_route.ArrivalBlocked) return "blocked by the crew question";
			if (_route.HoldOnly) return "approach hold only";
			var expected = Math.Atan2(-_route.FaceNormal.East, -_route.FaceNormal.North);
			return FirstContactSpeed >= 0 && FirstContactSpeed <= Units.KnotsToMetresPerSecond(1.5) &&
				!double.IsInfinity(_route.ForceLimit) && PeakForce > 0 && PeakForce <= _route.ForceLimit && !BottomedOut && FinalHoldOkay &&
				Math.Abs(Angles.WrapPi(state.Heading - expected)) <= Angles.ToRadians(5) && Failures.Count == 0 ? "pass" : "fail";
		}

		public const string HoldCsvHeader = "step,time_s,port_lever,starboard_lever,helm,port_engine_rad_s,starboard_engine_rad_s,port_shaft_rad_s,starboard_shaft_rad_s,port_gear,starboard_gear,port_throttle,starboard_throttle,intended_face_contact,grounded,heading_error_rad,side_force_n,sog_mps";

		private struct HoldSample
		{
			public long Step;
			public double Time, HeadingError, SideForce, Speed;
			public EngineTelemetry Port, Starboard;
			public ControlInputs Controls;
			public bool IntendedFace, Grounded;
		}

		private HoldSample HoldAt(int index) => _holdSamples[(_holdCursor - _holdCount + index + 1000) % 1000];

		/// <summary>Called after stepping only; the physics callback does not format strings or perform I/O.</summary>
		public void WriteHoldCsv(TextWriter writer)
		{
			writer.WriteLine(HoldCsvHeader);
			for (var i = 0; i < _holdCount; i++)
			{
				var r = HoldAt(i);
				writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
					"{0},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R},{7:R},{8:R},{9},{10},{11:R},{12:R},{13},{14},{15:R},{16:R},{17:R}",
					r.Step, r.Time, r.Controls.PortLever, r.Controls.StarboardLever, r.Controls.Helm, r.Port.EngineSpeed, r.Starboard.EngineSpeed,
					r.Port.ShaftSpeed, r.Starboard.ShaftSpeed, r.Port.Gear, r.Starboard.Gear, r.Port.Throttle, r.Starboard.Throttle,
					r.IntendedFace ? 1 : 0, r.Grounded ? 1 : 0, r.HeadingError, r.SideForce, r.Speed));
			}
		}

		public void AddHoldSummary(JObject result)
		{
			var intended = 0; var grounded = 0; var inGear = 0;
			var heading = 0.0; var side = 0.0; var port = 0.0; var starboard = 0.0;
			for (var i = 0; i < _holdCount; i++)
			{
				var r = HoldAt(i);
				if (r.IntendedFace) intended++;
				if (r.Grounded) grounded++;
				if (r.Port.Gear == GearState.Ahead || r.Port.Gear == GearState.Astern || r.Starboard.Gear == GearState.Ahead || r.Starboard.Gear == GearState.Astern) inGear++;
				heading = Math.Max(heading, Math.Abs(r.HeadingError)); side = Math.Max(side, Math.Abs(r.SideForce));
				port += r.Port.EngineSpeed; starboard += r.Starboard.EngineSpeed;
			}
			result["hold_window_steps"] = _holdCount;
			result["hold_window_intended_face_steps"] = intended;
			result["hold_window_grounded_steps"] = grounded;
			result["hold_window_bad_steps"] = _holdBadCount;
			result["hold_window_engines_in_gear_steps"] = inGear;
			result["hold_window_heading_error_abs_rad_max"] = _holdCount == 0 ? JValue.CreateNull() : new JValue(heading);
			result["hold_window_side_force_abs_n_max"] = _holdCount == 0 ? JValue.CreateNull() : new JValue(side);
			result["hold_window_port_engine_rad_s_mean"] = _holdCount == 0 ? JValue.CreateNull() : new JValue(port / _holdCount);
			result["hold_window_starboard_engine_rad_s_mean"] = _holdCount == 0 ? JValue.CreateNull() : new JValue(starboard / _holdCount);
		}

		public void BeforeStep(long step, in VesselState state, in VesselTelemetry telemetry, in ControlInputs controls) { _beforeSpeed = state.GroundVelocity.Magnitude; _beforeControls = controls; }
		public void AfterStep(long step, in VesselState state, in VesselTelemetry telemetry)
		{
			MinimumUnderKeel = Math.Min(MinimumUnderKeel, telemetry.DepthUnderKeel);
			PeakForce = Math.Max(PeakForce, telemetry.ContactForce);
			var intendedFace = _route.IntendedFaceContact(_simulation);
			var bad = !intendedFace || _simulation.Grounding.Grounded || telemetry.SpeedOverGround >= Units.KnotsToMetresPerSecond(.1);
			var expected = Math.Atan2(-_route.FaceNormal.East, -_route.FaceNormal.North);
			var forces = _simulation.LastForces;
			_holdSamples[_holdCursor] = new HoldSample { Step = step, Time = state.Time, Port = telemetry.Port, Starboard = telemetry.Starboard,
				Controls = _beforeControls, IntendedFace = intendedFace, Grounded = _simulation.Grounding.Grounded,
				HeadingError = Angles.WrapPi(state.Heading - expected), Speed = telemetry.SpeedOverGround,
				SideForce = forces.HullY + forces.PropellerY + forces.RudderY + forces.WindY + forces.ContactY + forces.GroundingY };
			if (_holdBad[_holdCursor]) _holdBadCount--;
			_holdBad[_holdCursor] = bad; if (bad) _holdBadCount++;
			_holdCursor = (_holdCursor + 1) % 1000; _holdCount = Math.Min(1000, _holdCount + 1);
			if (_simulation.Grounding.Grounded) GroundedSteps++;
			for (var i = 0; i < _crossed.Length; i++)
			{
				if (_crossed[i] || !HarborJourney.Inside(state.Position, _route.SpanRings[i])) continue;
				_crossed[i] = true;
				if (Crossings.Count != i) Failures.AddCrossing(_route.SpanIds[i]);
				Crossings.Add(_route.SpanIds[i]);
			}
			if (_simulation.ContactCount == 0) return;
			if (FirstContactSpeed < 0)
			{
				FirstContactSpeed = _beforeSpeed; FirstContactId = _simulation.GetContact(0).SegmentId;
				if (FirstContactSpeed > Units.KnotsToMetresPerSecond(1.5)) Failures.Add("First contact exceeds 1.5 kn");
			}
			for (var i = 0; i < _simulation.ContactCount; i++)
			{
				var contact = _simulation.GetContact(i);
				PeakCompression = Math.Max(PeakCompression, contact.Compression);
				BottomedOut |= contact.BottomedOut;
				var id = contact.SegmentId;
				if (Array.IndexOf(_route.FaceIds, id) < 0 && Failures.Count < 20) Failures.AddContact(id, step);
			}
		}
	}

	/// <summary>Bounded failure records; messages are formatted only when evidence is read after stepping.</summary>
	public sealed class JourneyFailures : IReadOnlyList<string>
	{
		private readonly Failure[] _items = new Failure[20];
		private struct Failure { public string Message, Span; public int Segment; public long Step; }
		public int Count { get; private set; }
		public string this[int index]
		{
			get
			{
				if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
				var f = _items[index];
				return f.Message ?? (f.Span != null ? "Bridge crossing out of order: " + f.Span : "Unintended contact " + f.Segment + " at step " + f.Step);
			}
		}
		public void Add(string message) { if (Count < _items.Length) _items[Count++] = new Failure { Message = message }; }
		public void AddCrossing(string span) { if (Count < _items.Length) _items[Count++] = new Failure { Span = span }; }
		public void AddContact(int segment, long step) { if (Count < _items.Length) _items[Count++] = new Failure { Segment = segment, Step = step }; }
		public IEnumerator<string> GetEnumerator() { for (var i = 0; i < Count; i++) yield return this[i]; }
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

}
