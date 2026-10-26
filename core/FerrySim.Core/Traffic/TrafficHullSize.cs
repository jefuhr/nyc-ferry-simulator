using System;

namespace FerrySim.Core.Traffic
{
	/// <summary>
	/// The hull the simulator gives a traffic vessel: a rectangle of its length and beam centred on the reported position, and a
	/// draft for drawing. AIS length and beam are used where they are plausible: length 2 to 460 m, beam 1 to 80 m and no more
	/// than the length. A missing or implausible dimension comes from the other one by the class's length-to-beam ratio, or,
	/// with neither, from the class's default hull. The defaults and ratios are estimates for New York harbor traffic
	/// (README.md, Hull sizes), not data. MarineCadastre gives AIS length and width but not the antenna's offsets (A, B, C, D),
	/// so the rectangle is centred on the reported position, which can be off the hull's middle by up to half its length.
	/// </summary>
	public readonly struct TrafficHullSize
	{
		/// <summary>Shortest and longest AIS length used, m.</summary>
		public const double MinimumLength = 2.0;

		public const double MaximumLength = 460.0;

		/// <summary>Narrowest and widest AIS beam used, m.</summary>
		public const double MinimumBeam = 1.0;

		public const double MaximumBeam = 80.0;

		/// <summary>Deepest AIS draught used, m.</summary>
		public const double MaximumDraft = 25.0;

		// Per class, in TrafficClass order: default length, beam and draft (m), and length-to-beam ratio. Estimates.
		private static readonly double[] DefaultLength = { 15.0, 26.0, 25.0, 150.0, 120.0, 10.0, 10.0, 15.0, 12.0, 16.0, 12.0, 30.0 };
		private static readonly double[] DefaultBeam = { 5.0, 8.0, 9.0, 25.0, 20.0, 3.5, 3.5, 5.0, 4.0, 5.0, 4.0, 7.0 };
		private static readonly double[] DefaultDraft = { 1.5, 1.5, 3.5, 8.0, 7.0, 1.0, 1.5, 2.0, 1.0, 1.5, 1.0, 2.0 };
		private static readonly double[] LengthToBeam = { 3.5, 3.25, 2.8, 6.5, 6.0, 3.0, 3.0, 3.5, 3.0, 3.2, 3.0, 4.5 };

		public TrafficHullSize(double length, double beam, double draft, bool lengthFromAis, bool beamFromAis, bool draftFromAis)
		{
			Length = length;
			Beam = beam;
			Draft = draft;
			LengthFromAis = lengthFromAis;
			BeamFromAis = beamFromAis;
			DraftFromAis = draftFromAis;
		}

		/// <summary>Length overall, m.</summary>
		public double Length { get; }

		/// <summary>Beam, m.</summary>
		public double Beam { get; }

		/// <summary>Draft, m: for drawing only.</summary>
		public double Draft { get; }

		/// <summary>True when the length is the vessel's AIS length, false when it is an estimate.</summary>
		public bool LengthFromAis { get; }

		public bool BeamFromAis { get; }

		public bool DraftFromAis { get; }

		/// <summary>Half the rectangle's diagonal, m: no part of the hull is farther than this from the reported position.</summary>
		public double Radius => 0.5 * Math.Sqrt(Length * Length + Beam * Beam);

		/// <summary>The class's default hull, when AIS gives no usable dimension.</summary>
		public static TrafficHullSize Default(TrafficClass vesselClass)
		{
			var c = Clamp(vesselClass);
			return new TrafficHullSize(DefaultLength[c], DefaultBeam[c], DefaultDraft[c], false, false, false);
		}

		/// <summary>The class's length-to-beam ratio, for a dimension AIS left out.</summary>
		public static double Ratio(TrafficClass vesselClass) => LengthToBeam[Clamp(vesselClass)];

		/// <summary>The hull for AIS dimensions (NaN where AIS gave none) of a vessel of <paramref name="vesselClass"/>.</summary>
		public static TrafficHullSize For(TrafficClass vesselClass, double length, double beam, double draft)
		{
			var c = Clamp(vesselClass);
			var lengthKnown = length >= MinimumLength && length <= MaximumLength;
			var beamKnown = beam >= MinimumBeam && beam <= MaximumBeam && (!lengthKnown || beam <= length);
			var draftKnown = draft > 0.0 && draft <= MaximumDraft;
			double l, b;
			if (lengthKnown && beamKnown)
			{
				l = length;
				b = beam;
			}
			else if (lengthKnown)
			{
				l = length;
				b = Math.Max(MinimumBeam, length / LengthToBeam[c]);
			}
			else if (beamKnown)
			{
				b = beam;
				l = Math.Min(MaximumLength, beam * LengthToBeam[c]);
			}
			else
			{
				l = DefaultLength[c];
				b = DefaultBeam[c];
			}

			return new TrafficHullSize(l, b, draftKnown ? draft : DefaultDraft[c], lengthKnown, beamKnown, draftKnown);
		}

		private static int Clamp(TrafficClass vesselClass)
		{
			var c = (int)vesselClass;
			return c >= 0 && c < DefaultLength.Length ? c : 0;
		}
	}
}
