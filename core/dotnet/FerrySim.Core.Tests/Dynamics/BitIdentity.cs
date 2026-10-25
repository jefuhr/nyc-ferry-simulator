using System;
using System.Text;
using FerrySim.Core.Dynamics;

namespace FerrySim.Core.Tests.Dynamics
{
	/// <summary>Compares two simulations in every bit of state, telemetry, forces, contact and grounding.</summary>
	public static class BitIdentity
	{
		/// <summary>The quantities of a and b whose bits differ, as text; empty when every bit agrees.</summary>
		public static string Differences(VesselDynamicsSimulation a, VesselDynamicsSimulation b)
		{
			var text = new StringBuilder();
			var x = a.State;
			var y = b.State;
			Compare(text, "time", x.Time, y.Time);
			Compare(text, "north", x.North, y.North);
			Compare(text, "east", x.East, y.East);
			Compare(text, "down", x.Down, y.Down);
			Compare(text, "roll", x.Roll, y.Roll);
			Compare(text, "pitch", x.Pitch, y.Pitch);
			Compare(text, "heading", x.Heading, y.Heading);
			Compare(text, "surge", x.Surge, y.Surge);
			Compare(text, "sway", x.Sway, y.Sway);
			Compare(text, "heave", x.Heave, y.Heave);
			Compare(text, "roll rate", x.RollRate, y.RollRate);
			Compare(text, "pitch rate", x.PitchRate, y.PitchRate);
			Compare(text, "yaw rate", x.YawRate, y.YawRate);
			var s = a.Telemetry;
			var t = b.Telemetry;
			Compare(text, "contact force", s.ContactForce, t.ContactForce);
			Compare(text, "speed through water", s.SpeedThroughWater, t.SpeedThroughWater);
			Compare(text, "SOG", s.SpeedOverGround, t.SpeedOverGround);
			Compare(text, "COG", s.CourseOverGround, t.CourseOverGround);
			Compare(text, "depth under keel", s.DepthUnderKeel, t.DepthUnderKeel);
			Compare(text, "rudder", s.PortRudderAngle, t.PortRudderAngle);
			Compare(text, "port engine", s.Port.EngineSpeed, t.Port.EngineSpeed);
			Compare(text, "port shaft", s.Port.ShaftSpeed, t.Port.ShaftSpeed);
			Compare(text, "starboard engine", s.Starboard.EngineSpeed, t.Starboard.EngineSpeed);
			Compare(text, "starboard shaft", s.Starboard.ShaftSpeed, t.Starboard.ShaftSpeed);
			var f = a.LastForces;
			var g = b.LastForces;
			Compare(text, "hull X", f.HullX, g.HullX);
			Compare(text, "hull Y", f.HullY, g.HullY);
			Compare(text, "hull N", f.HullN, g.HullN);
			Compare(text, "propeller X", f.PropellerX, g.PropellerX);
			Compare(text, "propeller N", f.PropellerN, g.PropellerN);
			Compare(text, "rudder Y", f.RudderY, g.RudderY);
			Compare(text, "wind Y", f.WindY, g.WindY);
			Compare(text, "contact X", f.ContactX, g.ContactX);
			Compare(text, "grounding X", f.GroundingX, g.GroundingX);
			Compare(text, "grounding N", f.GroundingN, g.GroundingN);
			var p = a.ContactLoad;
			var q = b.ContactLoad;
			Compare(text, "load north", p.North, q.North);
			Compare(text, "load east", p.East, q.East);
			Compare(text, "load N", p.N, q.N);
			var m = a.Grounding;
			var n = b.Grounding;
			if (m.Grounded != n.Grounded)
			{
				text.Append("grounded flag; ");
			}

			Compare(text, "penetration", m.Penetration, n.Penetration);
			Compare(text, "bed load", m.BedLoad, n.BedLoad);
			Compare(text, "bed force north", m.Load.North, n.Load.North);
			Compare(text, "bed force east", m.Load.East, n.Load.East);
			Compare(text, "bed moment", m.Load.N, n.Load.N);
			if (a.ContactCount != b.ContactCount)
			{
				text.Append($"contacts {a.ContactCount} and {b.ContactCount}; ");
			}

			return text.ToString();
		}

		private static void Compare(StringBuilder text, string name, double a, double b)
		{
			if (BitConverter.DoubleToInt64Bits(a) != BitConverter.DoubleToInt64Bits(b))
			{
				text.Append($"{name} {a:R} and {b:R}; ");
			}
		}
	}
}
