using System;
using System.Collections.Generic;
using FerrySim.Sim;
using UnityEngine;

namespace FerrySim.World
{
	/// <summary>The navigation lights of a power-driven vessel (Inland Navigation Rules, 33 CFR part 83).</summary>
	public enum NavigationLightKind
	{
		/// <summary>The (forward) masthead light, Rule 23(a)(i).</summary>
		Masthead = 0,

		/// <summary>The second masthead light, abaft of and higher than the forward one, Rule 23(a)(ii).</summary>
		AfterMasthead = 1,

		PortSidelight = 2,

		StarboardSidelight = 3,

		Sternlight = 4,

		/// <summary>The all-round white anchor light in the fore part, Rule 30(a)(i), or the single one of Rule 30(b).</summary>
		ForwardAnchor = 5,

		/// <summary>The lower all-round white anchor light at or near the stern, Rule 30(a)(ii).</summary>
		AfterAnchor = 6,
	}

	/// <summary>Colours of signal lights, as Annex I (33 CFR 84.13) and the light characters of aids and bridges name them.</summary>
	public enum SignalColour
	{
		White = 0,
		Red = 1,
		Green = 2,
		Yellow = 3,
	}

	/// <summary>
	/// What a vessel's lights show. A vessel underway shows Rule 23's lights whether or not it is making way; at anchor it
	/// shows Rule 30's. Moored means made fast to the shore, which Rule 3(i) says is not underway: the Rules prescribe no
	/// lights for a power-driven vessel moored alongside, so none are shown.
	/// </summary>
	public enum VesselLightStatus
	{
		Underway = 0,
		AtAnchor = 1,
		Moored = 2,
	}

	/// <summary>
	/// How a light's intensity falls off at one edge of its sector (Annex I, 33 CFR 84.15(a)): full intensity to
	/// <see cref="FullInsideDegrees"/> inside the edge, then linear to <see cref="AtLimit"/> at the edge, then linear to
	/// zero at <see cref="CutOffOutsideDegrees"/> outside it.
	/// </summary>
	public readonly struct LightSectorEdge
	{
		public LightSectorEdge(double fullInsideDegrees, double atLimit, double cutOffOutsideDegrees)
		{
			FullInsideDegrees = fullInsideDegrees;
			AtLimit = atLimit;
			CutOffOutsideDegrees = cutOffOutsideDegrees;
		}

		/// <summary>
		/// A sidelight's edge right ahead: full intensity up to the edge and a practical cut-off 2° outside it (84.15(a)(i):
		/// between 1° and 3° outside), so dead ahead both sidelights show.
		/// </summary>
		public static LightSectorEdge SidelightForward => new LightSectorEdge(0.0, 1.0, 2.0);

		/// <summary>
		/// Every other edge: masthead lights and sternlights, and the sidelights' edge 22.5° abaft the beam. Full intensity to
		/// 5° within the sector, half at its limit, practical cut-off 2° outside it (84.15(a)(ii): within 5° outside).
		/// </summary>
		public static LightSectorEdge Standard => new LightSectorEdge(5.0, 0.5, 2.0);

		public double FullInsideDegrees { get; }

		public double AtLimit { get; }

		public double CutOffOutsideDegrees { get; }

		/// <summary>Factor at <paramref name="degrees"/> inside the edge.</summary>
		public double Inside(double degrees) =>
			degrees >= FullInsideDegrees ? 1.0 : AtLimit + (1.0 - AtLimit) * degrees / FullInsideDegrees;

		/// <summary>Factor at <paramref name="degrees"/> outside the edge.</summary>
		public double Outside(double degrees) =>
			degrees >= CutOffOutsideDegrees ? 0.0 : AtLimit * (1.0 - degrees / CutOffOutsideDegrees);
	}

	/// <summary>
	/// The arc of the horizon a light shows over, from <see cref="StartDegrees"/> clockwise to <see cref="EndDegrees"/>, in
	/// relative bearings: degrees clockwise from right ahead, seen from above, as Rule 21 states its arcs. Bearings are
	/// those of the observer from the light.
	/// </summary>
	public readonly struct LightSector
	{
		public LightSector(double startDegrees, double endDegrees, LightSectorEdge startEdge, LightSectorEdge endEdge)
		{
			StartDegrees = Wrap(startDegrees);
			EndDegrees = Wrap(endDegrees);
			StartEdge = startEdge;
			EndEdge = endEdge;
			IsAllRound = false;
		}

		private LightSector(bool allRound)
		{
			StartDegrees = 0.0;
			EndDegrees = 0.0;
			StartEdge = default;
			EndEdge = default;
			IsAllRound = allRound;
		}

		/// <summary>An unbroken light over 360° (Rule 21(e)).</summary>
		public static LightSector AllRound => new LightSector(true);

		/// <summary>Rule 21(a): from right ahead to 22.5° abaft the beam on either side, 225°.</summary>
		public static LightSector Masthead => new LightSector(247.5, 112.5, LightSectorEdge.Standard, LightSectorEdge.Standard);

		/// <summary>Rule 21(b): green, from right ahead to 22.5° abaft the starboard beam, 112.5°.</summary>
		public static LightSector StarboardSidelight => new LightSector(0.0, 112.5, LightSectorEdge.SidelightForward, LightSectorEdge.Standard);

		/// <summary>Rule 21(b): red, from 22.5° abaft the port beam to right ahead, 112.5°.</summary>
		public static LightSector PortSidelight => new LightSector(247.5, 360.0, LightSectorEdge.Standard, LightSectorEdge.SidelightForward);

		/// <summary>Rule 21(c): 67.5° from right aft on each side, 135°.</summary>
		public static LightSector Sternlight => new LightSector(112.5, 247.5, LightSectorEdge.Standard, LightSectorEdge.Standard);

		public double StartDegrees { get; }

		public double EndDegrees { get; }

		public LightSectorEdge StartEdge { get; }

		public LightSectorEdge EndEdge { get; }

		public bool IsAllRound { get; }

		/// <summary>The sector's width, degrees: 360 for an all-round light.</summary>
		public double ArcDegrees => IsAllRound ? 360.0 : Wrap(EndDegrees - StartDegrees);

		/// <summary>The bearing midway through the sector, degrees.</summary>
		public double CentreDegrees => IsAllRound ? 0.0 : Wrap(StartDegrees + 0.5 * ArcDegrees);

		/// <summary>Intensity factor, 0 to 1, for an observer at <paramref name="relativeBearingDegrees"/> from the light.</summary>
		public double Factor(double relativeBearingDegrees)
		{
			if (IsAllRound)
			{
				return 1.0;
			}

			var bearing = Wrap(relativeBearingDegrees);
			var arc = ArcDegrees;
			var into = Wrap(bearing - StartDegrees);
			if (into <= arc)
			{
				return Math.Min(StartEdge.Inside(into), EndEdge.Inside(arc - into));
			}

			var pastEnd = into - arc;
			var beforeStart = 360.0 - into;
			return pastEnd <= beforeStart ? EndEdge.Outside(pastEnd) : StartEdge.Outside(beforeStart);
		}

		/// <summary>The observer's bearing from a light, degrees clockwise from right ahead, for a vector in the vessel's frame (x starboard, z forward).</summary>
		public static double RelativeBearingDegrees(Vector3 lightToObserver) =>
			Wrap(Math.Atan2(lightToObserver.x, lightToObserver.z) * 180.0 / Math.PI);

		internal static double Wrap(double degrees)
		{
			var wrapped = degrees % 360.0;
			return wrapped < 0.0 ? wrapped + 360.0 : wrapped;
		}
	}

	/// <summary>One navigation light: what it is, its colour and sector, its range by Rule 22 and where it is on the vessel.</summary>
	public readonly struct NavigationLightSpec
	{
		public NavigationLightSpec(NavigationLightKind kind, SignalColour colour, LightSector sector, double nominalRangeMetres, Vector3 position)
		{
			Kind = kind;
			Colour = colour;
			Sector = sector;
			NominalRangeMetres = nominalRangeMetres;
			Position = position;
		}

		public NavigationLightKind Kind { get; }

		public SignalColour Colour { get; }

		public LightSector Sector { get; }

		/// <summary>The minimum range of Rule 22, metres: the light's luminous range in Annex I's clear air.</summary>
		public double NominalRangeMetres { get; }

		/// <summary>Annex I's minimum intensity for that range (33 CFR 84.14), candelas.</summary>
		public double IntensityCandela => SignalLightOptics.IntensityForRange(NominalRangeMetres);

		/// <summary>In the vessel's frame: metres starboard (x), above the waterline (y) and forward of midships (z).</summary>
		public Vector3 Position { get; }
	}

	/// <summary>
	/// Where a vessel carries its lights, in its frame (x starboard, y up from the waterline, z forward from midships),
	/// metres, and its length overall for Rule 22's ranges. <see cref="Generic"/> lays them out from plain particulars for
	/// any power-driven hull; <see cref="ForGreybox"/> puts them on the learner's greybox. Every position is a greybox
	/// estimate, not a survey of a real vessel.
	/// </summary>
	public readonly struct VesselLightLayout
	{
		public VesselLightLayout(double lengthOverall, Vector3 masthead, Vector3 afterMasthead, Vector3 starboardSidelight, Vector3 sternlight, Vector3 forwardAnchor, Vector3 afterAnchor)
		{
			LengthOverall = lengthOverall;
			Masthead = masthead;
			AfterMasthead = afterMasthead;
			StarboardSidelight = starboardSidelight;
			Sternlight = sternlight;
			ForwardAnchor = forwardAnchor;
			AfterAnchor = afterAnchor;
		}

		public double LengthOverall { get; }

		public Vector3 Masthead { get; }

		/// <summary>Only shown on a vessel of 50 m or more (Rule 23(a)(ii)).</summary>
		public Vector3 AfterMasthead { get; }

		public Vector3 StarboardSidelight { get; }

		/// <summary>The starboard sidelight mirrored across the centreline.</summary>
		public Vector3 PortSidelight => new Vector3(-StarboardSidelight.x, StarboardSidelight.y, StarboardSidelight.z);

		public Vector3 Sternlight { get; }

		/// <summary>The single all-round anchor light of a vessel under 50 m (Rule 30(b)), or the forward one of two.</summary>
		public Vector3 ForwardAnchor { get; }

		/// <summary>The after anchor light of a vessel of 50 m or more (Rule 30(a)(ii)).</summary>
		public Vector3 AfterAnchor { get; }

		/// <summary>
		/// A layout from plain particulars, for any power-driven hull (traffic included): the masthead light on the centreline
		/// a quarter of the length forward of midships at <paramref name="mastheadHeight"/> above the water; on a vessel of
		/// 50 m or more the after masthead light half the length (at most 50 m, at least a quarter) further aft and 4.5 m
		/// higher (84.03(a) and 84.02(a)(ii) ask at least a quarter and 2 m; 4.5 m also meets the International Rules); the
		/// sidelights at the vessel's sides (84.03(b)) a tenth of the length forward of midships, so never ahead of the
		/// masthead light, at least 1 m below it (84.02(g)); the sternlight on the centreline at the stern, 1 m above the
		/// deck; and the anchor lights at the masthead light's height (one light) or at the bow and stern, the forward one
		/// 4.5 m above the after one and at least 6 m above the hull (84.02(k)). All greybox estimates.
		/// </summary>
		public static VesselLightLayout Generic(double lengthOverall, double beam, double mastheadHeight, double freeboard)
		{
			if (!(lengthOverall > 0.0) || !(beam > 0.0) || !(mastheadHeight > freeboard) || !(freeboard >= 0.0))
			{
				throw new ArgumentException($"Light layout needs a positive length and beam and a masthead light above the deck, got length {lengthOverall}, beam {beam}, masthead {mastheadHeight}, freeboard {freeboard}.");
			}

			var length = (float)lengthOverall;
			var masthead = new Vector3(0f, (float)mastheadHeight, 0.25f * length);
			var separation = Mathf.Min(0.5f * length, Mathf.Max(0.25f * length, 50f));
			var afterMasthead = new Vector3(0f, (float)mastheadHeight + 4.5f, masthead.z - separation);
			var sidelightHeight = (float)Math.Max(freeboard + 0.5, Math.Min(mastheadHeight - 1.0, freeboard + 0.6 * (mastheadHeight - freeboard)));
			var sidelight = new Vector3(0.5f * (float)beam, sidelightHeight, 0.1f * length);
			var stern = new Vector3(0f, (float)freeboard + 1f, -0.5f * length + 0.3f);
			Vector3 forwardAnchor;
			Vector3 afterAnchor;
			if (lengthOverall >= NavigationLightRules.LongVesselMetres)
			{
				var forwardHeight = (float)freeboard + 6f;
				forwardAnchor = new Vector3(0f, forwardHeight, 0.45f * length);
				afterAnchor = new Vector3(0f, forwardHeight - 4.5f, -0.45f * length);
			}
			else
			{
				forwardAnchor = masthead + new Vector3(0f, 0.6f, 0f);
				afterAnchor = forwardAnchor;
			}

			return new VesselLightLayout(lengthOverall, masthead, afterMasthead, sidelight, stern, forwardAnchor, afterAnchor);
		}

		/// <summary>
		/// The learner's greybox (<see cref="GreyboxDimensions"/>): the masthead light on a 1.5 m mast on the centreline of the
		/// wheelhouse roof; the sidelights at the outer edges of the cabin top, 0.5 m up, abreast the wheelhouse's forward
		/// part, so not ahead of the masthead light; the sternlight on the centreline at the after end of the deck, 1 m up;
		/// and the anchor light 0.6 m above the masthead light. Under 50 m, so one masthead light (Rule 23(a)(ii)) and one
		/// anchor light (Rule 30(b)). Greybox estimates: the real lanterns' positions are a crew question.
		/// </summary>
		public static VesselLightLayout ForGreybox(GreyboxDimensions d)
		{
			var deckTop = d.Freeboard;
			var wheelhouseCentre = d.BridgeEyeForward - 1.0;
			var wheelhouseTop = deckTop + d.CabinHeight + 2.2;
			var masthead = new Vector3(0f, (float)(wheelhouseTop + 1.5), (float)wheelhouseCentre);
			var sidelight = new Vector3((float)(0.5 * (d.Beam - 0.4)), (float)(deckTop + d.CabinHeight + 0.5), (float)(wheelhouseCentre - 0.5));
			var deckAftEdge = -0.02 * d.LengthOverall - 0.47 * d.LengthOverall;
			var stern = new Vector3(0f, (float)(deckTop + 1.0), (float)(deckAftEdge + 0.1));
			var anchor = masthead + new Vector3(0f, 0.6f, 0f);
			return new VesselLightLayout(d.LengthOverall, masthead, masthead + new Vector3(0f, 4.5f, (float)(-0.5 * d.LengthOverall)), sidelight, stern, anchor, anchor);
		}
	}

	/// <summary>
	/// The lights a power-driven vessel shows, by the Inland Navigation Rules (33 CFR part 83, text quoted in
	/// World/Atmosphere/README.md): Rule 23(a) underway, Rule 30(a) and (b) at anchor, with Rule 21's sectors, Rule 22's
	/// ranges by length and Annex I's intensities (33 CFR 84.14).
	/// </summary>
	public static class NavigationLightRules
	{
		/// <summary>Rule 22(a), Rule 23(a)(ii), Rule 30(b): the length from which the larger rules apply, metres.</summary>
		public const double LongVesselMetres = 50.0;

		/// <summary>Rule 22(b) and (c): metres.</summary>
		public const double SmallVesselMetres = 12.0;

		/// <summary>Rule 22(b)(i): a masthead light's range drops to 3 miles under this length, metres.</summary>
		public const double ShortMastheadRangeMetres = 20.0;

		public const double MetresPerNauticalMile = 1852.0;

		/// <summary>Rule 22: the minimum range of a light on a vessel of <paramref name="lengthOverall"/> metres, nautical miles.</summary>
		public static double NominalRangeNauticalMiles(NavigationLightKind kind, double lengthOverall)
		{
			var masthead = kind == NavigationLightKind.Masthead || kind == NavigationLightKind.AfterMasthead;
			var sidelight = kind == NavigationLightKind.PortSidelight || kind == NavigationLightKind.StarboardSidelight;
			if (lengthOverall >= LongVesselMetres)
			{
				// (a): masthead 6 miles; sidelight, sternlight and all-round lights 3 miles.
				return masthead ? 6.0 : 3.0;
			}

			if (lengthOverall >= SmallVesselMetres)
			{
				// (b): masthead 5 miles (3 under 20 m); sidelight, sternlight and all-round lights 2 miles.
				return masthead ? (lengthOverall < ShortMastheadRangeMetres ? 3.0 : 5.0) : 2.0;
			}

			// (c): masthead 2 miles; sidelight 1 mile; sternlight and all-round lights 2 miles.
			return sidelight ? 1.0 : 2.0;
		}

		/// <summary>Rule 21: the sector a kind of light shows over.</summary>
		public static LightSector Sector(NavigationLightKind kind)
		{
			switch (kind)
			{
				case NavigationLightKind.Masthead:
				case NavigationLightKind.AfterMasthead:
					return LightSector.Masthead;
				case NavigationLightKind.PortSidelight:
					return LightSector.PortSidelight;
				case NavigationLightKind.StarboardSidelight:
					return LightSector.StarboardSidelight;
				case NavigationLightKind.Sternlight:
					return LightSector.Sternlight;
				default:
					return LightSector.AllRound;
			}
		}

		/// <summary>Rule 21: masthead, stern and anchor lights are white, the port sidelight red and the starboard one green.</summary>
		public static SignalColour Colour(NavigationLightKind kind) =>
			kind == NavigationLightKind.PortSidelight ? SignalColour.Red
			: kind == NavigationLightKind.StarboardSidelight ? SignalColour.Green
			: SignalColour.White;

		/// <summary>
		/// Fills <paramref name="lights"/> (cleared first) with the lights the vessel shows: underway, a masthead light, a
		/// second one from 50 m, sidelights and a sternlight (Rule 23(a)); at anchor, one all-round white light under 50 m and
		/// two from 50 m (Rule 30(a), (b)); moored, none.
		/// </summary>
		public static void Lights(in VesselLightLayout layout, VesselLightStatus status, List<NavigationLightSpec> lights)
		{
			if (lights == null)
			{
				throw new ArgumentNullException(nameof(lights));
			}

			lights.Clear();
			var length = layout.LengthOverall;
			switch (status)
			{
				case VesselLightStatus.Underway:
					Add(lights, NavigationLightKind.Masthead, layout.Masthead, length);
					if (length >= LongVesselMetres)
					{
						Add(lights, NavigationLightKind.AfterMasthead, layout.AfterMasthead, length);
					}

					Add(lights, NavigationLightKind.PortSidelight, layout.PortSidelight, length);
					Add(lights, NavigationLightKind.StarboardSidelight, layout.StarboardSidelight, length);
					Add(lights, NavigationLightKind.Sternlight, layout.Sternlight, length);
					break;
				case VesselLightStatus.AtAnchor:
					Add(lights, NavigationLightKind.ForwardAnchor, layout.ForwardAnchor, length);
					if (length >= LongVesselMetres)
					{
						Add(lights, NavigationLightKind.AfterAnchor, layout.AfterAnchor, length);
					}

					break;
			}
		}

		private static void Add(List<NavigationLightSpec> lights, NavigationLightKind kind, Vector3 position, double length) =>
			lights.Add(new NavigationLightSpec(kind, Colour(kind), Sector(kind), NominalRangeNauticalMiles(kind, length) * MetresPerNauticalMile, position));
	}
}
