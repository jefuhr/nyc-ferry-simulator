using System;
using FerrySim.Core.Contracts;
using FerrySim.Core.Traffic;
using FerrySim.World;
using UnityEngine;

namespace FerrySim.Traffic
{
	/// <summary>What a traffic hull is doing, for its lights (Rule 23 underway, Rule 30 at anchor or made fast) and its drawing.</summary>
	public enum TrafficHullStatus
	{
		/// <summary>Moving through the harbor.</summary>
		Underway = 0,

		/// <summary>A moving vessel stopped for now (speed over ground under <see cref="TrafficHull.StoppedSpeed"/>): AIS cannot say whether it is made fast.</summary>
		Stopped = 1,

		/// <summary>A vessel the pack calls static (every report within 50 m of its median): moored or at anchor all day.</summary>
		Moored = 2,
	}

	/// <summary>
	/// One traffic vessel's greybox hull, on the run's clock (<see cref="TrafficView"/> places it). Public for whatever dresses a
	/// hull, such as navigation lights: who it is, its size and what it is doing. SI units, headings clockwise from true north. The
	/// hull's origin is its reported position at the waterline; x is starboard, y up and z forward, as every Unity vessel here.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class TrafficHull : MonoBehaviour
	{
		/// <summary>Below this speed over ground, m/s (about 0.5 kn), a moving vessel counts as stopped.</summary>
		public const double StoppedSpeed = 0.25;

		/// <summary>The vessel's index in the day's pack.</summary>
		public int VesselIndex { get; private set; }

		public int Mmsi { get; private set; }

		/// <summary>The AIS name, empty when AIS gave none.</summary>
		public string VesselName { get; private set; } = string.Empty;

		public TrafficClass Class { get; private set; }

		/// <summary>The pack's class name, for example <c>tug-tow</c>.</summary>
		public string ClassName => TrafficClasses.Name(Class);

		/// <summary>Length overall, m: AIS, or the class's default (<see cref="LengthFromAis"/>).</summary>
		public double Length { get; private set; }

		/// <summary>Beam, m: AIS, or the class's default (<see cref="BeamFromAis"/>).</summary>
		public double Beam { get; private set; }

		/// <summary>Draft, m, below the waterline: drawn only.</summary>
		public double Draft { get; private set; }

		/// <summary>Height of the greybox's top above the waterline, m: an estimate by class, where a masthead light would sit.</summary>
		public double Height { get; private set; }

		public bool LengthFromAis { get; private set; }

		public bool BeamFromAis { get; private set; }

		/// <summary>False for a vessel the pack calls static, which is drawn without moving.</summary>
		public bool Moving { get; private set; }

		/// <summary>True for one of the fleet's T and K ferries.</summary>
		public bool FerryCandidate { get; private set; }

		/// <summary>True while the vessel is in the run's traffic now and drawn.</summary>
		public bool Shown { get; private set; }

		/// <summary>The drawn position, world (north, east) m.</summary>
		public double North { get; private set; }

		public double East { get; private set; }

		/// <summary>Speed over the ground, m/s.</summary>
		public double SpeedOverGround { get; private set; }

		/// <summary>Course over the ground, rad; NaN when stopped.</summary>
		public double CourseOverGround { get; private set; } = double.NaN;

		/// <summary>True heading, rad, as the replay gives it (<see cref="HeadingSource"/>).</summary>
		public double Heading { get; private set; }

		public HeadingSource HeadingSource { get; private set; }

		public TrafficMotion Motion { get; private set; }

		public TrafficHullStatus Status { get; private set; }

		/// <summary>True while it is under the CPA alarm, and while it is in collision with the learner's hull.</summary>
		public bool Alarmed { get; private set; }

		public bool Colliding { get; private set; }

		/// <summary>The power-driven light set, built once with this hull.</summary>
		public NavigationLights NavigationLights { get; private set; }

		private Renderer[] _hullRenderers;
		public bool DetailedHullShown { get; private set; } = true;
		public double MaximumLightIntensity { get; private set; }

		internal void ShowDetail(bool show)
		{
			if (DetailedHullShown == show) return;
			DetailedHullShown = show;
			for (var i = 0; i < _hullRenderers.Length; i++) _hullRenderers[i].enabled = show;
		}

		internal void Describe(TrafficVessel vessel, double height)
		{
			_hullRenderers = GetComponentsInChildren<Renderer>(true);
			VesselIndex = vessel.Index;
			Mmsi = vessel.Mmsi;
			VesselName = vessel.Name;
			Class = vessel.Class;
			Length = vessel.Hull.Length;
			Beam = vessel.Hull.Beam;
			Draft = vessel.Hull.Draft;
			LengthFromAis = vessel.Hull.LengthFromAis;
			BeamFromAis = vessel.Hull.BeamFromAis;
			Moving = vessel.Moving;
			FerryCandidate = vessel.FerryCandidate;
			Height = height;
			NavigationLights = gameObject.AddComponent<NavigationLights>();
			// The pack has no navigational status. Static vessels retain the existing moored classification.
			// A 1.5 m mast and a deck at 40% of the greybox height are visual estimates, not AIS particulars.
			NavigationLights.ConfigureGeneric(Length, Beam, Height + 1.5, Height * 0.4,
				Moving ? VesselLightStatus.Underway : VesselLightStatus.Moored);
			NavigationLights.On = false;
			for (var i = 0; i < NavigationLights.Specs.Count; i++)
				MaximumLightIntensity = Math.Max(MaximumLightIntensity, NavigationLights.Specs[i].IntensityCandela);
		}

		internal void Follow(in TrafficSample sample, bool alarmed, bool colliding)
		{
			Shown = true;
			North = sample.North;
			East = sample.East;
			var speed = sample.SpeedOverGround;
			SpeedOverGround = speed;
			CourseOverGround = speed >= StoppedSpeed ? Angles.WrapTwoPi(Math.Atan2(sample.VelocityEast, sample.VelocityNorth)) : double.NaN;
			Heading = sample.Heading;
			HeadingSource = sample.HeadingSource;
			Motion = sample.Motion;
			Status = !Moving ? TrafficHullStatus.Moored : speed < StoppedSpeed ? TrafficHullStatus.Stopped : TrafficHullStatus.Underway;
			Alarmed = alarmed;
			Colliding = colliding;
		}

		internal void Hide()
		{
			Shown = false;
			Alarmed = false;
			Colliding = false;
		}
	}
}
