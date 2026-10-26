using System.Collections.Generic;
using System.Linq;
using FerrySim.Sim;
using FerrySim.World;
using NUnit.Framework;
using UnityEngine;

namespace FerrySim.Slice.Tests
{
	/// <summary>
	/// The lights a power-driven vessel shows (Inland Rules 21, 22, 23 and 30; Annex I, 33 CFR 84). Failure modes guarded:
	/// sidelights reversed (green to port or red to starboard, or placed on the wrong side); a sector of the wrong width or
	/// turned (the sternlight seen from ahead, the masthead light from astern); sidelights that do not overlap dead ahead
	/// or edges that never cut off; Rule 22's ranges taken for the wrong length; intensities that do not match Annex I's
	/// table; a second masthead light missing on a long vessel; anchor or moored vessels showing underway lights; and a
	/// greybox layout that breaks Annex I's placement rules.
	/// </summary>
	public class NavigationLightRulesTests
	{
		private const double Tolerance = 1e-9;

		[TestCase(0.0, 1.0)]
		[TestCase(90.0, 1.0)]
		[TestCase(107.5, 1.0)]
		[TestCase(110.0, 0.75)]
		[TestCase(112.5, 0.5)]
		[TestCase(113.5, 0.25)]
		[TestCase(114.5, 0.0)]
		[TestCase(180.0, 0.0)]
		[TestCase(245.5, 0.0)]
		[TestCase(247.5, 0.5)]
		[TestCase(252.5, 1.0)]
		[TestCase(270.0, 1.0)]
		public void Masthead_ShowsFromRightAheadTo22Point5DegreesAbaftEitherBeam(double bearing, double factor)
		{
			Assert.That(LightSector.Masthead.ArcDegrees, Is.EqualTo(225.0).Within(Tolerance), "Rule 21(a): 225 degrees");
			Assert.That(LightSector.Masthead.Factor(bearing), Is.EqualTo(factor).Within(Tolerance));
		}

		[TestCase(0.0, 1.0)]
		[TestCase(359.0, 0.5)]
		[TestCase(358.0, 0.0)]
		[TestCase(45.0, 1.0)]
		[TestCase(90.0, 1.0)]
		[TestCase(112.5, 0.5)]
		[TestCase(114.5, 0.0)]
		[TestCase(180.0, 0.0)]
		[TestCase(270.0, 0.0)]
		[TestCase(315.0, 0.0)]
		public void StarboardSidelight_ShowsFromRightAheadToAbaftTheStarboardBeam_AndCutsOffWithinThreeDegreesAhead(double bearing, double factor)
		{
			Assert.That(LightSector.StarboardSidelight.ArcDegrees, Is.EqualTo(112.5).Within(Tolerance), "Rule 21(b): 112.5 degrees");
			Assert.That(LightSector.StarboardSidelight.Factor(bearing), Is.EqualTo(factor).Within(Tolerance));
		}

		[TestCase(0.0, 1.0)]
		[TestCase(1.0, 0.5)]
		[TestCase(2.0, 0.0)]
		[TestCase(315.0, 1.0)]
		[TestCase(270.0, 1.0)]
		[TestCase(247.5, 0.5)]
		[TestCase(245.5, 0.0)]
		[TestCase(180.0, 0.0)]
		[TestCase(90.0, 0.0)]
		[TestCase(45.0, 0.0)]
		public void PortSidelight_MirrorsTheStarboardOne(double bearing, double factor)
		{
			Assert.That(LightSector.PortSidelight.ArcDegrees, Is.EqualTo(112.5).Within(Tolerance));
			Assert.That(LightSector.PortSidelight.Factor(bearing), Is.EqualTo(factor).Within(Tolerance));
		}

		[TestCase(180.0, 1.0)]
		[TestCase(117.5, 1.0)]
		[TestCase(112.5, 0.5)]
		[TestCase(110.5, 0.0)]
		[TestCase(242.5, 1.0)]
		[TestCase(247.5, 0.5)]
		[TestCase(249.5, 0.0)]
		[TestCase(0.0, 0.0)]
		[TestCase(90.0, 0.0)]
		public void Sternlight_Shows67Point5DegreesFromRightAftOnEachSide(double bearing, double factor)
		{
			Assert.That(LightSector.Sternlight.ArcDegrees, Is.EqualTo(135.0).Within(Tolerance), "Rule 21(c): 135 degrees");
			Assert.That(LightSector.Sternlight.Factor(bearing), Is.EqualTo(factor).Within(Tolerance));
		}

		[Test]
		public void TheSectors_TileTheHorizon_DeadAheadShowsBothSidelights_AndAnAllRoundLightShowsEverywhere()
		{
			for (var bearing = 0.0; bearing < 360.0; bearing += 0.5)
			{
				var masthead = LightSector.Masthead.Factor(bearing);
				var stern = LightSector.Sternlight.Factor(bearing);
				Assert.That(masthead + stern, Is.GreaterThanOrEqualTo(0.5 - Tolerance), $"a white light shows at {bearing} degrees");
				Assert.That(LightSector.AllRound.Factor(bearing), Is.EqualTo(1.0));
			}

			Assert.That(LightSector.PortSidelight.Factor(0.0), Is.EqualTo(1.0));
			Assert.That(LightSector.StarboardSidelight.Factor(0.0), Is.EqualTo(1.0));
		}

		[Test]
		public void TheObserversBearing_IsMeasuredClockwiseFromTheBow_WithStarboardPositive()
		{
			Assert.That(LightSector.RelativeBearingDegrees(new Vector3(0f, 0f, 100f)), Is.EqualTo(0.0).Within(1e-6));
			Assert.That(LightSector.RelativeBearingDegrees(new Vector3(100f, 0f, 0f)), Is.EqualTo(90.0).Within(1e-6));
			Assert.That(LightSector.RelativeBearingDegrees(new Vector3(0f, 5f, -100f)), Is.EqualTo(180.0).Within(1e-6));
			Assert.That(LightSector.RelativeBearingDegrees(new Vector3(-100f, 0f, 0f)), Is.EqualTo(270.0).Within(1e-6));
			Assert.That(LightSector.RelativeBearingDegrees(new Vector3(100f, 0f, 100f)), Is.EqualTo(45.0).Within(1e-6));
		}

		[TestCase(60.0, 6.0, 3.0, 3.0, 3.0)]
		[TestCase(50.0, 6.0, 3.0, 3.0, 3.0)]
		[TestCase(29.6, 5.0, 2.0, 2.0, 2.0)]
		[TestCase(26.0096, 5.0, 2.0, 2.0, 2.0)]
		[TestCase(15.0, 3.0, 2.0, 2.0, 2.0)]
		[TestCase(12.0, 3.0, 2.0, 2.0, 2.0)]
		[TestCase(10.0, 2.0, 1.0, 2.0, 2.0)]
		public void Ranges_FollowRule22ByLength(double length, double masthead, double sidelight, double sternlight, double allRound)
		{
			Assert.That(NavigationLightRules.NominalRangeNauticalMiles(NavigationLightKind.Masthead, length), Is.EqualTo(masthead));
			Assert.That(NavigationLightRules.NominalRangeNauticalMiles(NavigationLightKind.AfterMasthead, length), Is.EqualTo(masthead));
			Assert.That(NavigationLightRules.NominalRangeNauticalMiles(NavigationLightKind.PortSidelight, length), Is.EqualTo(sidelight));
			Assert.That(NavigationLightRules.NominalRangeNauticalMiles(NavigationLightKind.StarboardSidelight, length), Is.EqualTo(sidelight));
			Assert.That(NavigationLightRules.NominalRangeNauticalMiles(NavigationLightKind.Sternlight, length), Is.EqualTo(sternlight));
			Assert.That(NavigationLightRules.NominalRangeNauticalMiles(NavigationLightKind.ForwardAnchor, length), Is.EqualTo(allRound));
		}

		[TestCase(1.0, 0.9)]
		[TestCase(2.0, 4.3)]
		[TestCase(3.0, 12.0)]
		[TestCase(4.0, 27.0)]
		[TestCase(5.0, 52.0)]
		[TestCase(6.0, 94.0)]
		public void Intensities_MatchAnnexIsTable(double rangeNauticalMiles, double candelas)
		{
			// 33 CFR 84.14(b) rounds to two significant figures.
			var intensity = SignalLightOptics.IntensityForRange(rangeNauticalMiles * NavigationLightRules.MetresPerNauticalMile);
			Assert.That(intensity, Is.EqualTo(candelas).Within(0.05 * candelas + 0.01));
		}

		[Test]
		public void Underway_ShowsAMastheadLightSidelightsAndASternlight_InTheirColours_SidelightsOnTheirOwnSides()
		{
			var lights = Lights(VesselLightLayout.Generic(30.0, 8.5, 9.0, 2.0), VesselLightStatus.Underway);

			Assert.That(lights.Select(l => l.Kind), Is.EqualTo(new[]
			{
				NavigationLightKind.Masthead, NavigationLightKind.PortSidelight, NavigationLightKind.StarboardSidelight, NavigationLightKind.Sternlight,
			}), "Rule 23(a); a vessel under 50 m need not show a second masthead light");
			var port = lights.Single(l => l.Kind == NavigationLightKind.PortSidelight);
			var starboard = lights.Single(l => l.Kind == NavigationLightKind.StarboardSidelight);
			Assert.That(port.Colour, Is.EqualTo(SignalColour.Red));
			Assert.That(starboard.Colour, Is.EqualTo(SignalColour.Green));
			Assert.That(port.Position.x, Is.LessThan(0f), "red on the port side (Unity -x, with z forward)");
			Assert.That(starboard.Position.x, Is.GreaterThan(0f), "green on the starboard side (Unity +x)");
			Assert.That(lights.Single(l => l.Kind == NavigationLightKind.Masthead).Colour, Is.EqualTo(SignalColour.White));
			Assert.That(lights.Single(l => l.Kind == NavigationLightKind.Sternlight).Colour, Is.EqualTo(SignalColour.White));
			Assert.That(port.NominalRangeMetres, Is.EqualTo(2.0 * 1852.0));
			Assert.That(lights.Single(l => l.Kind == NavigationLightKind.Masthead).NominalRangeMetres, Is.EqualTo(5.0 * 1852.0));
		}

		[Test]
		public void AnObserverOffTheStarboardBow_SeesGreenAndTheMastheadLight_NotRedOrTheSternlight()
		{
			var lights = Lights(VesselLightLayout.Generic(30.0, 8.5, 9.0, 2.0), VesselLightStatus.Underway);
			var observer = new Vector3(300f, 2f, 300f);

			var seen = lights.Where(l => l.Sector.Factor(LightSector.RelativeBearingDegrees(observer - l.Position)) > 0.0).Select(l => l.Kind).ToList();

			Assert.That(seen, Is.EquivalentTo(new[] { NavigationLightKind.Masthead, NavigationLightKind.StarboardSidelight }));
		}

		[Test]
		public void ALongVessel_ShowsTwoMastheadLights_TheAfterOneHigher_AtLeastAQuarterOfHerLengthApart()
		{
			var layout = VesselLightLayout.Generic(120.0, 20.0, 18.0, 6.0);
			var lights = Lights(layout, VesselLightStatus.Underway);
			var forward = lights.Single(l => l.Kind == NavigationLightKind.Masthead).Position;
			var after = lights.Single(l => l.Kind == NavigationLightKind.AfterMasthead).Position;

			Assert.That(lights, Has.Count.EqualTo(5));
			Assert.That(after.y - forward.y, Is.GreaterThanOrEqualTo(2f), "84.02(a)(ii): at least 2 m higher");
			Assert.That(forward.z - after.z, Is.GreaterThanOrEqualTo(0.25f * 120f - 1e-3f), "84.03(a): at least a quarter of the length apart");
			Assert.That(forward.z, Is.GreaterThan(0f), "84.03(a): the forward light within half the length from the stem");
			Assert.That(lights.Single(l => l.Kind == NavigationLightKind.Masthead).NominalRangeMetres, Is.EqualTo(6.0 * 1852.0));
			Assert.That(lights.Single(l => l.Kind == NavigationLightKind.Sternlight).NominalRangeMetres, Is.EqualTo(3.0 * 1852.0));
		}

		[Test]
		public void AtAnchor_ShowsAllRoundWhiteLightsOnly_AndMooredShowsNone()
		{
			const float longFreeboard = 6f;
			var shortAnchor = Lights(VesselLightLayout.Generic(30.0, 8.5, 9.0, 2.0), VesselLightStatus.AtAnchor);
			var longAnchor = Lights(VesselLightLayout.Generic(120.0, 20.0, 18.0, longFreeboard), VesselLightStatus.AtAnchor);
			var moored = Lights(VesselLightLayout.Generic(30.0, 8.5, 9.0, 2.0), VesselLightStatus.Moored);

			Assert.That(shortAnchor.Select(l => l.Kind), Is.EqualTo(new[] { NavigationLightKind.ForwardAnchor }), "Rule 30(b): one all-round white light under 50 m");
			Assert.That(longAnchor.Select(l => l.Kind), Is.EqualTo(new[] { NavigationLightKind.ForwardAnchor, NavigationLightKind.AfterAnchor }), "Rule 30(a)");
			Assert.That(shortAnchor.Concat(longAnchor).All(l => l.Sector.IsAllRound && l.Colour == SignalColour.White), Is.True);
			var forward = longAnchor[0].Position;
			var after = longAnchor[1].Position;
			Assert.That(forward.y - after.y, Is.GreaterThanOrEqualTo(4.5f - 1e-4f), "84.02(k): the forward one at least 4.5 m above the after one");
			Assert.That(forward.y - longFreeboard, Is.GreaterThanOrEqualTo(6f - 1e-4f), "84.02(k): at least 6 m above the hull from 50 m");
			Assert.That(forward.z, Is.GreaterThan(after.z), "in the fore part, and the lower one at or near the stern");
			Assert.That(moored, Is.Empty, "made fast to the shore is not underway (Rule 3(i)); no lights are prescribed");
		}

		[TestCase("vessels/t-class.json")]
		[TestCase("vessels/k-class.json")]
		public void TheLearnersGreyboxLayout_KeepsAnnexIsPlacementRules(string vesselFile)
		{
			var vessel = FerrySim.Core.Serialization.VesselParametersJson.Load(System.IO.Path.Combine(SliceTestData.RepoRoot, vesselFile));
			var d = GreyboxDimensions.From(vessel);
			var layout = VesselLightLayout.ForGreybox(d);
			var lights = Lights(layout, VesselLightStatus.Underway);
			var masthead = lights.Single(l => l.Kind == NavigationLightKind.Masthead).Position;
			var starboard = lights.Single(l => l.Kind == NavigationLightKind.StarboardSidelight).Position;
			var port = lights.Single(l => l.Kind == NavigationLightKind.PortSidelight).Position;
			var stern = lights.Single(l => l.Kind == NavigationLightKind.Sternlight).Position;

			Assert.That(lights, Has.Count.EqualTo(4), "under 50 m: one masthead light");
			Assert.That(masthead.x, Is.EqualTo(0f), "Rule 21(a): over the centreline");
			Assert.That(masthead.z, Is.GreaterThan(0f), "84.03(d): forward of amidships");
			Assert.That(masthead.y, Is.GreaterThan((float)(d.Freeboard + d.CabinHeight + 2.2)), "above the wheelhouse, clear of the other lights (84.02(f))");
			Assert.That(masthead.y - starboard.y, Is.GreaterThanOrEqualTo(1f), "84.02(g): sidelights at least 1 m lower");
			Assert.That(starboard.z, Is.LessThanOrEqualTo(masthead.z), "84.03(b): not in front of the masthead light");
			Assert.That(port.x, Is.EqualTo(-starboard.x));
			Assert.That(starboard.x, Is.GreaterThan(0.4f * (float)d.Beam), "84.03(b): at or near the side");
			var baseAngle = Mathf.Atan2(masthead.y - starboard.y, starboard.x) * Mathf.Rad2Deg;
			Assert.That(baseAngle, Is.GreaterThanOrEqualTo(27f), "84.19(a): a high-speed craft's masthead light may be lower if the end-elevation base angle is at least 27 degrees");
			Assert.That(stern.x, Is.EqualTo(0f));
			Assert.That(stern.z, Is.LessThan(-0.45f * (float)d.LengthOverall), "Rule 21(c): as nearly as practicable at the stern");
			Assert.That(stern.y, Is.GreaterThan((float)d.Freeboard), "above the deck");
		}

		private static List<NavigationLightSpec> Lights(VesselLightLayout layout, VesselLightStatus status)
		{
			var lights = new List<NavigationLightSpec>();
			NavigationLightRules.Lights(layout, status, lights);
			return lights;
		}
	}
}
