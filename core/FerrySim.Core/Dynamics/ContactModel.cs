using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>
	/// Normal force per metre of contact length against total compression, for a landing fender and the
	/// hull's own fendering pressed together in series. Each side is a spring with a damper up to its
	/// compression limit. Past the limit it bottoms out: the structure behind it takes the load, its
	/// stiffness rises by the bottomed stiffness factor and its damping by that factor's square root, so its
	/// damping ratio is unchanged. The series law is piecewise linear: both soft, the first bottomed out, both
	/// bottomed out.
	/// <para>
	/// The springs combine in series. The dampers combine so that they dissipate the same power when the
	/// compression splits between the sides in proportion to their compliances, c = Σ cᵢ φᵢ² with φᵢ the
	/// compliance share. That is exact when both sides have the same c/k, and keeps one damped side
	/// dissipating when the other has no damping. Past each bottoming point the damping blends linearly into
	/// the next range's value over <see cref="DampingBlend"/>, so the damping force, like the elastic force,
	/// has no jump while the hull moves.
	/// </para>
	/// <para>
	/// <see cref="Hysteresis"/> h is the rubber's rate-independent loss: the contact model scales the
	/// elastic force by 1 + h while the fenders compress and 1 − h while they recover.
	/// </para>
	/// </summary>
	public readonly struct FenderLaw
	{
		/// <summary>Compression past each bottoming point over which the damping blends into the next range's value, m.</summary>
		public const double DampingBlend = 0.01;

		private readonly double _stiffness1;
		private readonly double _stiffness2;
		private readonly double _stiffness3;
		private readonly double _damping1;
		private readonly double _damping2;
		private readonly double _damping3;
		private readonly double _force1;
		private readonly double _force2;

		private FenderLaw(double stiffness1, double stiffness2, double stiffness3, double damping1, double damping2, double damping3,
			double force1, double force2, double friction, double hysteresis)
		{
			_stiffness1 = stiffness1;
			_stiffness2 = stiffness2;
			_stiffness3 = stiffness3;
			_damping1 = damping1;
			_damping2 = damping2;
			_damping3 = damping3;
			_force1 = force1;
			_force2 = force2;
			BottomingCompression = force1 / stiffness1;
			FullCompression = BottomingCompression + (force2 - force1) / stiffness2;
			Friction = friction;
			Hysteresis = hysteresis;
		}

		/// <summary>Total compression at which the first side bottoms out, m.</summary>
		public double BottomingCompression { get; }

		/// <summary>Total compression at which both sides have bottomed out, m.</summary>
		public double FullCompression { get; }

		/// <summary>Coulomb friction coefficient, from the landing's surface.</summary>
		public double Friction { get; }

		/// <summary>Fraction of the elastic force added while compressing and removed while recovering.</summary>
		public double Hysteresis { get; }

		/// <summary>The law for a landing surface against hull fendering with the given per-metre values.</summary>
		public static FenderLaw Combine(in ContactSurface landing, double hullStiffness, double hullDamping, double hullMaxCompression, double bottomedStiffnessFactor,
			double hysteresis)
		{
			if (!(hullStiffness > 0.0) || !(hullDamping >= 0.0) || !(hullMaxCompression > 0.0) || !(bottomedStiffnessFactor >= 1.0) ||
				double.IsInfinity(hullStiffness) || double.IsInfinity(hullDamping) || double.IsInfinity(hullMaxCompression) || double.IsInfinity(bottomedStiffnessFactor))
			{
				throw new ArgumentException("Hull fender values must be finite, with positive stiffness and compression limit, non-negative damping and a bottomed stiffness factor of at least 1.");
			}

			if (!(hysteresis >= 0.0 && hysteresis < 1.0))
			{
				throw new ArgumentException("Fender hysteresis must be at least 0 and below 1, so a recovering fender still pushes.");
			}

			var landingLimit = landing.Stiffness * landing.MaxCompression;
			var hullLimit = hullStiffness * hullMaxCompression;
			var landingFirst = landingLimit <= hullLimit;
			var firstStiffness = landingFirst ? landing.Stiffness : hullStiffness;
			var firstDamping = landingFirst ? landing.Damping : hullDamping;
			var secondStiffness = landingFirst ? hullStiffness : landing.Stiffness;
			var secondDamping = landingFirst ? hullDamping : landing.Damping;
			var dampingFactor = Math.Sqrt(bottomedStiffnessFactor);

			Series(firstStiffness, firstDamping, secondStiffness, secondDamping, out var stiffness1, out var damping1);
			Series(firstStiffness * bottomedStiffnessFactor, firstDamping * dampingFactor, secondStiffness, secondDamping, out var stiffness2, out var damping2);
			Series(firstStiffness * bottomedStiffnessFactor, firstDamping * dampingFactor, secondStiffness * bottomedStiffnessFactor, secondDamping * dampingFactor,
				out var stiffness3, out var damping3);
			return new FenderLaw(stiffness1, stiffness2, stiffness3, damping1, damping2, damping3,
				Math.Min(landingLimit, hullLimit), Math.Max(landingLimit, hullLimit), landing.Friction, hysteresis);
		}

		/// <summary>Elastic force per metre of contact length at a total compression, N/m.</summary>
		public double Force(double compression)
		{
			if (compression <= BottomingCompression)
			{
				return _stiffness1 * compression;
			}

			if (compression <= FullCompression)
			{
				return _force1 + _stiffness2 * (compression - BottomingCompression);
			}

			return _force2 + _stiffness3 * (compression - FullCompression);
		}

		/// <summary>Slope of <see cref="Force"/>, N/m per m.</summary>
		public double Stiffness(double compression) =>
			compression <= BottomingCompression ? _stiffness1 : compression <= FullCompression ? _stiffness2 : _stiffness3;

		/// <summary>
		/// Damping per metre of contact length at a total compression, N·s/m per m: the range's value, blended
		/// into the next over <see cref="DampingBlend"/> past each bottoming point. A weighted mean of the three
		/// ranges' values, so never negative.
		/// </summary>
		public double Damping(double compression)
		{
			if (compression <= BottomingCompression)
			{
				return _damping1;
			}

			var damping = _damping1 + (_damping2 - _damping1) * Math.Min(1.0, (compression - BottomingCompression) / DampingBlend);
			if (compression > FullCompression)
			{
				damping += (_damping3 - _damping2) * Math.Min(1.0, (compression - FullCompression) / DampingBlend);
			}

			return damping;
		}

		private static void Series(double stiffnessA, double dampingA, double stiffnessB, double dampingB, out double stiffness, out double damping)
		{
			var complianceA = 1.0 / stiffnessA;
			var complianceB = 1.0 / stiffnessB;
			var compliance = complianceA + complianceB;
			var shareA = complianceA / compliance;
			var shareB = complianceB / compliance;
			stiffness = 1.0 / compliance;
			damping = dampingA * shareA * shareA + dampingB * shareB * shareB;
		}
	}

	/// <summary>One segment's contact with the hull, from the last recorded evaluation. SI units.</summary>
	public readonly struct SegmentContact
	{
		public SegmentContact(int segmentId, double normalForce, double frictionForce, double compression, double contactLength, bool bottomedOut)
		{
			SegmentId = segmentId;
			NormalForce = normalForce;
			FrictionForce = frictionForce;
			Compression = compression;
			ContactLength = contactLength;
			BottomedOut = bottomedOut;
		}

		/// <summary>The touched segment's <see cref="ContactSegment.Id"/>.</summary>
		public int SegmentId { get; }

		/// <summary>Normal force on the hull, along the segment's water normal, N (positive).</summary>
		public double NormalForce { get; }

		/// <summary>Friction force on the hull along the segment's direction (Start toward End), N.</summary>
		public double FrictionForce { get; }

		/// <summary>Deepest total compression of the hull and landing fenders anywhere along the contact, m.</summary>
		public double Compression { get; }

		/// <summary>Length of hull outline in contact, m.</summary>
		public double ContactLength { get; }

		/// <summary>True when a fender has passed its compression limit somewhere along the contact.</summary>
		public bool BottomedOut { get; }
	}

	/// <summary>Resultant contact load on the hull. Forces in N; the moment is about the reference point, N·m.</summary>
	public readonly struct ContactLoad
	{
		public ContactLoad(double north, double east, double moment, double heading)
		{
			var cos = Math.Cos(heading);
			var sin = Math.Sin(heading);
			North = north;
			East = east;
			N = moment;
			X = north * cos + east * sin;
			Y = -north * sin + east * cos;
			Magnitude = Math.Sqrt(north * north + east * east);
		}

		/// <summary>Body-axis surge force, N.</summary>
		public double X { get; }

		/// <summary>Body-axis sway force, N.</summary>
		public double Y { get; }

		/// <summary>Yaw moment about the reference point, N·m, positive turning to starboard.</summary>
		public double N { get; }

		/// <summary>World north component, N.</summary>
		public double North { get; }

		/// <summary>World east component, N.</summary>
		public double East { get; }

		/// <summary>Magnitude of the resultant force (what <see cref="VesselTelemetry.ContactForce"/> reports), N.</summary>
		public double Magnitude { get; }
	}

	/// <summary>
	/// Contact between the hull outline and fixed structures (<see cref="ContactSegment"/>s). See README.md,
	/// Contact.
	/// <list type="bullet">
	/// <item>The outline is the <c>contact</c> section's polylines, body axes, cut into cells of at most 0.1 m.
	/// A cell comes into contact with a face only by crossing it from the water side within the segment's
	/// length, and stays with that face, however deep, until it is back on the water side
	/// (<see cref="Commit"/>). A hull clear of a structure's near face is never caught by its far face, and a
	/// point deep in a thin structure is pushed back out the way it came in.</item>
	/// <item>Where a segment ends and the next one starts (a polyline, a corner of a barge), a cell that slides
	/// off the end passes to the next segment if it lies behind it within its length, and keeps its face in an
	/// inside corner behind both. Otherwise it is let go.</item>
	/// <item>Along each edge, the part of each run of cells in contact with one face is found exactly and
	/// integrated with the midpoint rule at about 0.2 m spacing, so separate patches each keep their load.</item>
	/// <item>Normal force per metre: the <see cref="FenderLaw"/> of the segment's surface in series with the
	/// hull fendering, times 1 ± h for hysteresis (smoothed through zero rate as δ̇/√(δ̇² + s₀²)), plus
	/// damping on the rate of compression, never pulling.</item>
	/// <item>Friction along the segment: −μ f_n s/√(s² + s₀²) for slip speed s, smooth through zero slip.</item>
	/// <item>Forces act at the contact points, so they turn the boat.</item>
	/// </list>
	/// Sized for a number of segments when built, and allocation-free after that.
	/// </summary>
	public sealed class ContactModel
	{
		/// <summary>
		/// Lowest speed regularising friction and hysteresis through zero slip and zero compression rate, m/s.
		/// Below it both act like stiff dampers.
		/// </summary>
		public const double MinimumSlipSpeed = 0.001;

		/// <summary>Longest stretch of outline one integration point stands for, m.</summary>
		private const double SampleSpacing = 0.2;

		/// <summary>Longest stretch of outline that shares one contact state, m.</summary>
		private const double CellLength = 0.1;

		/// <summary>Largest gap between one segment's end and the next one's start for the two to join, m.</summary>
		private const double JointTolerance = 0.001;

		// A neighbouring segment not looked up since the last Prepare.
		private const int Unknown = -2;

		private readonly int _edgeCount;
		private readonly double[] _edgeStartX;
		private readonly double[] _edgeStartY;
		private readonly double[] _edgeEndX;
		private readonly double[] _edgeEndY;
		private readonly double[] _edgeLength;
		private readonly double _hullStiffness;
		private readonly double _hullDamping;
		private readonly double _hullMaxCompression;
		private readonly double _bottomedFactor;
		private readonly double _hysteresis;

		// Outline cells: edge e has _edgeCells[e] equal cells from its start, numbered from _edgeFirstCell[e].
		// Each is in contact with one prepared segment or none (−1). An edge with no cell in contact and no
		// face within reach (_edgeNear, all edges after Prepare) is skipped.
		private readonly int[] _edgeFirstCell;
		private readonly int[] _edgeCells;
		private readonly int[] _edgeInContact;
		private readonly bool[] _edgeNear;
		private readonly int _cellCount;
		private readonly int[] _cellSegment;
		private int _cellsInContact;

		// The pose of the last Reset, Begin or Commit. New contact is a face crossed since then.
		private double _fromNorth;
		private double _fromEast;
		private double _fromHeading;
		private double _fromCos = 1.0;
		private double _fromSin;

		// Prepared segments, world axes. The water normal is (direction east, −direction north). The neighbours
		// are the segments that start where each ends and end where it starts (−1 for none).
		private readonly int _capacity;
		private int _segmentCount;
		private readonly ContactSegment[] _segment;
		private readonly double[] _startNorth;
		private readonly double[] _startEast;
		private readonly double[] _directionNorth;
		private readonly double[] _directionEast;
		private readonly double[] _segmentLength;
		private readonly FenderLaw[] _law;
		private readonly int[] _next;
		private readonly int[] _previous;

		// Segments where new contact can start: all of them after Prepare, those within reach after MayTouch.
		private readonly int[] _active;
		private readonly bool[] _near;
		private int _activeCount;

		// The segments in contact before a Prepare, to find among the new ones: by old index, the entry in the
		// held lists, or −1.
		private readonly int[] _heldEntry;
		private readonly ContactSegment[] _held;
		private readonly int[] _heldFrom;
		private readonly int[] _heldTo;

		// One edge at the pose being evaluated: its ends relative to the reference point (world axes) and the
		// reference point. For each of its cells, the face it touches in this evaluation and how much of it
		// entered that face since the last commit (infinite for a cell already in contact).
		private double _originNorth;
		private double _originEast;
		private double _aNorth;
		private double _aEast;
		private double _bNorth;
		private double _bEast;
		private readonly int[] _owner;
		private readonly double[] _entered;

		// Recorded by the last evaluation with record set.
		private readonly double[] _normalSum;
		private readonly double[] _frictionSum;
		private readonly double[] _lengthSum;
		private readonly double[] _deepest;
		private readonly SegmentContact[] _contacts;
		private int _contactCount;

		/// <summary>A model for up to <see cref="VesselDynamicsSimulation.MaximumSegments"/> segments at a time.</summary>
		public ContactModel(VesselParameters parameters)
			: this(parameters, VesselDynamicsSimulation.MaximumSegments)
		{
		}

		/// <summary>A model for up to <paramref name="segmentCapacity"/> segments at a time.</summary>
		public ContactModel(VesselParameters parameters, int segmentCapacity)
		{
			if (parameters == null)
			{
				throw new ArgumentNullException(nameof(parameters));
			}

			if (segmentCapacity < 1)
			{
				throw new ArgumentOutOfRangeException(nameof(segmentCapacity));
			}

			var section = Required.Section(parameters.Contact, "contact");
			_hullStiffness = Required.Positive(section.HullFenderStiffness, "contact.hullFenderStiffness");
			_hullDamping = Required.NonNegative(section.HullFenderDamping, "contact.hullFenderDamping");
			_hullMaxCompression = Required.Positive(section.HullFenderMaxCompression, "contact.hullFenderMaxCompression");
			_bottomedFactor = Required.InRange(section.BottomedStiffnessFactor, "contact.bottomedStiffnessFactor", 1.0, 1.0e4);
			_hysteresis = Required.InRange(section.FenderHysteresis, "contact.fenderHysteresis", 0.0, 0.9);

			var parts = new[] { section.StarboardDemihull, section.PortDemihull, section.BowFenderLine, section.SternFenderLine };
			var names = new[] { "contact.starboardDemihull", "contact.portDemihull", "contact.bowFenderLine", "contact.sternFenderLine" };
			for (var p = 0; p < parts.Length; p++)
			{
				var part = Required.Section(parts[p], names[p]);
				if (part.Count < 2)
				{
					throw new ArgumentException($"{names[p]} needs at least two points.");
				}

				_edgeCount += part.Count - 1;
			}

			_edgeStartX = new double[_edgeCount];
			_edgeStartY = new double[_edgeCount];
			_edgeEndX = new double[_edgeCount];
			_edgeEndY = new double[_edgeCount];
			_edgeLength = new double[_edgeCount];
			_edgeFirstCell = new int[_edgeCount];
			_edgeCells = new int[_edgeCount];
			_edgeInContact = new int[_edgeCount];
			_edgeNear = new bool[_edgeCount];
			var edge = 0;
			var widest = 0;
			for (var p = 0; p < parts.Length; p++)
			{
				var part = parts[p];
				Point(part[0], $"{names[p]}[0]", out var previousX, out var previousY);
				BoundingRadius = Math.Max(BoundingRadius, Math.Sqrt(previousX * previousX + previousY * previousY));
				for (var i = 1; i < part.Count; i++)
				{
					Point(part[i], $"{names[p]}[{i}]", out var x, out var y);
					var length = Math.Sqrt((x - previousX) * (x - previousX) + (y - previousY) * (y - previousY));
					if (!(length > 1e-6))
					{
						throw new ArgumentException($"{names[p]}[{i}] repeats the point before it.");
					}

					_edgeStartX[edge] = previousX;
					_edgeStartY[edge] = previousY;
					_edgeEndX[edge] = x;
					_edgeEndY[edge] = y;
					_edgeLength[edge] = length;
					_edgeFirstCell[edge] = _cellCount;
					_edgeCells[edge] = Math.Max(1, (int)Math.Ceiling(length / CellLength));
					_cellCount += _edgeCells[edge];
					widest = Math.Max(widest, _edgeCells[edge]);
					edge++;
					BoundingRadius = Math.Max(BoundingRadius, Math.Sqrt(x * x + y * y));
					previousX = x;
					previousY = y;
				}
			}

			_cellSegment = new int[_cellCount];
			_owner = new int[widest];
			_entered = new double[widest];

			_capacity = segmentCapacity;
			_segment = new ContactSegment[segmentCapacity];
			_startNorth = new double[segmentCapacity];
			_startEast = new double[segmentCapacity];
			_directionNorth = new double[segmentCapacity];
			_directionEast = new double[segmentCapacity];
			_segmentLength = new double[segmentCapacity];
			_law = new FenderLaw[segmentCapacity];
			_next = new int[segmentCapacity];
			_previous = new int[segmentCapacity];
			_active = new int[segmentCapacity];
			_near = new bool[segmentCapacity];
			_heldEntry = new int[segmentCapacity];
			var held = Math.Min(segmentCapacity, _cellCount);
			_held = new ContactSegment[held];
			_heldFrom = new int[held];
			_heldTo = new int[held];
			_normalSum = new double[segmentCapacity];
			_frictionSum = new double[segmentCapacity];
			_lengthSum = new double[segmentCapacity];
			_deepest = new double[segmentCapacity];
			_contacts = new SegmentContact[segmentCapacity];
			for (var j = 0; j < segmentCapacity; j++)
			{
				_heldEntry[j] = -1;
			}

			Reset(0.0, 0.0, 0.0);
		}

		/// <summary>Largest distance of an outline point from the reference point, m.</summary>
		public double BoundingRadius { get; }

		/// <summary>
		/// When false, fender hysteresis is left out altogether, whatever <c>contact.fenderHysteresis</c> says:
		/// the model without hysteresis, kept so the two can be compared in one build. With a hysteresis of 0
		/// they agree in every bit. True unless changed; change it before stepping.
		/// </summary>
		public bool UseHysteresis { get; set; } = true;

		/// <summary>Most segments <see cref="Prepare"/> takes at a time.</summary>
		public int SegmentCapacity => _capacity;

		/// <summary>Number of segments touching the hull in the last recorded evaluation.</summary>
		public int ContactCount => _contactCount;

		/// <summary>Resultant load of the last recorded evaluation.</summary>
		public ContactLoad LastLoad { get; private set; }

		/// <summary>One segment's contact from the last recorded evaluation, in prepared segment order.</summary>
		public SegmentContact Contact(int index)
		{
			if (index < 0 || index >= _contactCount)
			{
				throw new ArgumentOutOfRangeException(nameof(index));
			}

			return _contacts[index];
		}

		/// <summary>The fender law of a landing surface against this hull's fendering.</summary>
		public FenderLaw LawFor(in ContactSurface surface) =>
			FenderLaw.Combine(surface, _hullStiffness, _hullDamping, _hullMaxCompression, _bottomedFactor, _hysteresis);

		/// <summary>
		/// Takes the first <paramref name="count"/> segments of <paramref name="segments"/> as the structures
		/// to test against until the next call. The outline stays in contact with the segments it touched that
		/// are among them (same ends and id), and lets go of the others. More than
		/// <see cref="SegmentCapacity"/> segments is an error: the model never grows.
		/// </summary>
		public void Prepare(ContactSegment[] segments, int count)
		{
			if (segments == null)
			{
				throw new ArgumentNullException(nameof(segments));
			}

			if (count < 0 || count > segments.Length || count > _capacity)
			{
				throw new ArgumentOutOfRangeException(nameof(count), $"Between 0 and {_capacity} segments, and no more than the array holds.");
			}

			var held = 0;
			if (_cellsInContact > 0)
			{
				for (var c = 0; c < _cellCount; c++)
				{
					var old = _cellSegment[c];
					if (old >= 0 && _heldEntry[old] < 0)
					{
						_heldEntry[old] = held;
						_held[held] = _segment[old];
						_heldFrom[held] = old;
						held++;
					}
				}
			}

			for (var i = 0; i < count; i++)
			{
				var direction = segments[i].Direction;
				_segment[i] = segments[i];
				_startNorth[i] = segments[i].Start.North;
				_startEast[i] = segments[i].Start.East;
				_directionNorth[i] = direction.North;
				_directionEast[i] = direction.East;
				_segmentLength[i] = segments[i].Length;
				_law[i] = LawFor(segments[i].Surface);
				_next[i] = Unknown;
				_previous[i] = Unknown;
				_active[i] = i;
			}

			_segmentCount = count;
			_activeCount = count;
			for (var e = 0; e < _edgeCount; e++)
			{
				_edgeNear[e] = true;
			}

			if (held == 0)
			{
				return;
			}

			for (var h = 0; h < held; h++)
			{
				// The query usually returns the same segments in the same order.
				var from = _heldFrom[h];
				_heldTo[h] = from < count && Same(_segment[from], _held[h]) ? from : -1;
				for (var i = 0; i < count && _heldTo[h] < 0; i++)
				{
					if (Same(_segment[i], _held[h]))
					{
						_heldTo[h] = i;
					}
				}
			}

			for (var e = 0; e < _edgeCount; e++)
			{
				for (var c = _edgeFirstCell[e]; c < _edgeFirstCell[e] + _edgeCells[e]; c++)
				{
					var old = _cellSegment[c];
					if (old >= 0)
					{
						_cellSegment[c] = _heldTo[_heldEntry[old]];
						if (_cellSegment[c] < 0)
						{
							_cellsInContact--;
							_edgeInContact[e]--;
						}
					}
				}
			}

			for (var h = 0; h < held; h++)
			{
				_heldEntry[_heldFrom[h]] = -1;
			}
		}

		/// <summary>
		/// Forgets all contact, as for a hull placed at this pose: parts of it already behind a face are not in
		/// contact with that face until they have been back on its water side.
		/// </summary>
		public void Reset(double north, double east, double heading)
		{
			for (var c = 0; c < _cellCount; c++)
			{
				_cellSegment[c] = -1;
			}

			for (var e = 0; e < _edgeCount; e++)
			{
				_edgeInContact[e] = 0;
				_edgeNear[e] = true;
			}

			_cellsInContact = 0;
			Begin(north, east, heading);
			ClearRecord();
		}

		/// <summary>
		/// Sets the pose the hull moves on from, without changing contact: forces count faces crossed since
		/// then, and the next <see cref="Commit"/> keeps them.
		/// </summary>
		public void Begin(double north, double east, double heading)
		{
			_fromNorth = north;
			_fromEast = east;
			_fromHeading = heading;
			_fromCos = Math.Cos(heading);
			_fromSin = Math.Sin(heading);
		}

		/// <summary>
		/// The hull has moved to this pose since the last <see cref="Commit"/>, <see cref="Begin"/> or
		/// <see cref="Reset"/>. Parts of the outline that crossed a face from its water side, within the
		/// segment's length, come into contact with it. Parts in contact stay with their face while behind it,
		/// pass to the next segment of the same boundary when they slide off its end behind that one, and are let
		/// go once back on the water side.
		/// </summary>
		public void Commit(double north, double east, double heading)
		{
			if (_segmentCount > 0)
			{
				var cos = Math.Cos(heading);
				var sin = Math.Sin(heading);
				var moved = north != _fromNorth || east != _fromEast || heading != _fromHeading;
				for (var e = 0; e < _edgeCount; e++)
				{
					if (_edgeInContact[e] == 0 && !_edgeNear[e])
					{
						continue;
					}

					Edge(e, north, east, cos, sin);
					if (!Owners(e, moved, true) && _edgeInContact[e] == 0)
					{
						continue;
					}

					var first = _edgeFirstCell[e];
					var cells = _edgeCells[e];
					var k = 0;
					while (k < cells)
					{
						var j = _cellSegment[first + k];
						var end = k + 1;
						while (end < cells && _cellSegment[first + end] == j)
						{
							end++;
						}

						if (j >= 0)
						{
							var regions = default(Regions);
							Find(j, k / (double)cells, end / (double)cells, ref regions);
							for (var m = k; m < end; m++)
							{
								var cellStart = m / (double)cells;
								var cellEnd = (m + 1) / (double)cells;
								if (Overlap(cellStart, cellEnd, regions.Start, regions.End) > 0.0 ||
									Overlap(cellStart, cellEnd, regions.EndCornerStart, regions.EndCornerEnd) > 0.0 ||
									Overlap(cellStart, cellEnd, regions.StartCornerStart, regions.StartCornerEnd) > 0.0)
								{
									continue;
								}

								var onNext = Overlap(cellStart, cellEnd, regions.NextStart, regions.NextEnd);
								var onPrevious = Overlap(cellStart, cellEnd, regions.PreviousStart, regions.PreviousEnd);
								if (onNext > 0.0 || onPrevious > 0.0)
								{
									_cellSegment[first + m] = onNext >= onPrevious ? regions.Next : regions.Previous;
								}
								else if (_owner[m] >= 0)
								{
									_cellSegment[first + m] = _owner[m];
								}
								else
								{
									_cellSegment[first + m] = -1;
									_cellsInContact--;
									_edgeInContact[e]--;
								}
							}
						}
						else
						{
							for (var m = k; m < end; m++)
							{
								if (_owner[m] >= 0)
								{
									_cellSegment[first + m] = _owner[m];
									_cellsInContact++;
									_edgeInContact[e]++;
								}
							}
						}

						k = end;
					}
				}
			}

			Begin(north, east, heading);
		}

		/// <summary>Forgets the last recorded contacts, for a step with no contact.</summary>
		public void ClearRecord()
		{
			_contactCount = 0;
			LastLoad = default;
		}

		/// <summary>
		/// True when some part of the outline is in contact, or at this pose lies within
		/// <paramref name="reach"/> metres of a prepared segment's face, within its length give or take the
		/// reach. Until the next <see cref="Prepare"/> or <see cref="MayTouch"/>, new contact is looked for only
		/// with those segments, so the reach must cover the hull's motion until then.
		/// </summary>
		public bool MayTouch(double north, double east, double heading, double reach)
		{
			var cos = Math.Cos(heading);
			var sin = Math.Sin(heading);
			for (var j = 0; j < _segmentCount; j++)
			{
				_near[j] = false;
			}

			for (var e = 0; e < _edgeCount; e++)
			{
				var aNorth = north + _edgeStartX[e] * cos - _edgeStartY[e] * sin;
				var aEast = east + _edgeStartX[e] * sin + _edgeStartY[e] * cos;
				var bNorth = north + _edgeEndX[e] * cos - _edgeEndY[e] * sin;
				var bEast = east + _edgeEndX[e] * sin + _edgeEndY[e] * cos;
				_edgeNear[e] = false;
				for (var j = 0; j < _segmentCount; j++)
				{
					Locate(j, aNorth, aEast, out var alongA, out var depthA);
					Locate(j, bNorth, bEast, out var alongB, out var depthB);
					var length = _segmentLength[j];
					var t0 = 0.0;
					var t1 = 1.0;
					if (Clip(depthA + reach, depthB + reach, ref t0, ref t1) &&
						Clip(reach - depthA, reach - depthB, ref t0, ref t1) &&
						Clip(alongA + reach, alongB + reach, ref t0, ref t1) &&
						Clip(length + reach - alongA, length + reach - alongB, ref t0, ref t1))
					{
						_near[j] = true;
						_edgeNear[e] = true;
					}
				}
			}

			_activeCount = 0;
			for (var j = 0; j < _segmentCount; j++)
			{
				if (_near[j])
				{
					_active[_activeCount] = j;
					_activeCount++;
				}
			}

			return _activeCount > 0 || _cellsInContact > 0;
		}

		/// <summary>
		/// Σ (μ × normal force + h × elastic force) over the contact at this pose and velocity, N: the friction
		/// and hysteresis the integrator must carry through zero slip, used to choose the slip speed.
		/// </summary>
		public double FrictionLoad(double north, double east, double heading, double northRate, double eastRate, double yawRate) =>
			Evaluate(north, east, heading, northRate, eastRate, yawRate, MinimumSlipSpeed, false, false, out _, out _, out _);

		/// <summary>
		/// Contact forces on the hull in body axes at the reference point (N, N·m) for a pose and the ground
		/// velocity of the reference point (world north and east, m/s) and yaw rate (rad/s). The parts of the
		/// outline in contact, and those that crossed a face since the last <see cref="Commit"/>, push. With
		/// <paramref name="record"/> the per-segment contacts and the resultant are kept for telemetry.
		/// </summary>
		public void Forces(double north, double east, double heading, double northRate, double eastRate, double yawRate, double slipSpeed, bool record,
			out double x, out double y, out double n)
		{
			Evaluate(north, east, heading, northRate, eastRate, yawRate, slipSpeed, true, record, out var forceNorth, out var forceEast, out n);
			var cos = Math.Cos(heading);
			var sin = Math.Sin(heading);
			x = forceNorth * cos + forceEast * sin;
			y = -forceNorth * sin + forceEast * cos;
		}

		private double Evaluate(double north, double east, double heading, double northRate, double eastRate, double yawRate, double slipSpeed,
			bool friction, bool record, out double forceNorth, out double forceEast, out double moment)
		{
			forceNorth = 0.0;
			forceEast = 0.0;
			moment = 0.0;
			var load = 0.0;
			if (record)
			{
				for (var j = 0; j < _segmentCount; j++)
				{
					_normalSum[j] = 0.0;
					_frictionSum[j] = 0.0;
					_lengthSum[j] = 0.0;
					_deepest[j] = 0.0;
				}
			}

			var cos = Math.Cos(heading);
			var sin = Math.Sin(heading);
			var moved = north != _fromNorth || east != _fromEast || heading != _fromHeading;
			var edges = _segmentCount > 0 ? _edgeCount : 0;
			for (var e = 0; e < edges; e++)
			{
				if (_edgeInContact[e] == 0 && (!moved || !_edgeNear[e]))
				{
					continue;
				}

				Edge(e, north, east, cos, sin);
				if (!Owners(e, moved, false))
				{
					continue;
				}

				// Each run of cells touching one face: its part behind that face, and past the face's ends the next
				// segment's part and the inside corner.
				var cells = _edgeCells[e];
				var k = 0;
				while (k < cells)
				{
					var j = _owner[k];
					var end = k + 1;
					while (end < cells && _owner[end] == j)
					{
						end++;
					}

					if (j >= 0)
					{
						var r = default(Regions);
						Find(j, k / (double)cells, end / (double)cells, ref r);
						Integrate(e, j, r.Start, r.End, r.DepthA, r.DepthSlope, northRate, eastRate, yawRate, slipSpeed, friction, record,
							ref load, ref forceNorth, ref forceEast, ref moment);
						if (r.Next >= 0)
						{
							Integrate(e, r.Next, r.NextStart, r.NextEnd, r.NextDepthA, r.NextDepthSlope, northRate, eastRate, yawRate, slipSpeed, friction, record,
								ref load, ref forceNorth, ref forceEast, ref moment);
						}

						Integrate(e, j, r.EndCornerStart, r.EndCornerEnd, r.DepthA, r.DepthSlope, northRate, eastRate, yawRate, slipSpeed, friction, record,
							ref load, ref forceNorth, ref forceEast, ref moment);
						if (r.Previous >= 0)
						{
							Integrate(e, r.Previous, r.PreviousStart, r.PreviousEnd, r.PreviousDepthA, r.PreviousDepthSlope, northRate, eastRate, yawRate,
								slipSpeed, friction, record, ref load, ref forceNorth, ref forceEast, ref moment);
						}

						Integrate(e, j, r.StartCornerStart, r.StartCornerEnd, r.DepthA, r.DepthSlope, northRate, eastRate, yawRate, slipSpeed, friction, record,
							ref load, ref forceNorth, ref forceEast, ref moment);
					}

					k = end;
				}
			}

			if (record)
			{
				_contactCount = 0;
				for (var j = 0; j < _segmentCount; j++)
				{
					if (_lengthSum[j] > 0.0)
					{
						_contacts[_contactCount] = new SegmentContact(_segment[j].Id, _normalSum[j], _frictionSum[j], _deepest[j], _lengthSum[j],
							_deepest[j] > _law[j].BottomingCompression);
						_contactCount++;
					}
				}

				LastLoad = new ContactLoad(forceNorth, forceEast, moment, heading);
			}

			return load;
		}

		// Adds the forces from the part [t0, t1] of edge e behind segment j, whose depth there is
		// depthA + t × depthSlope, with the midpoint rule, and its friction and hysteresis load. Nothing when t0
		// is not below t1.
		private void Integrate(int e, int j, double t0, double t1, double depthA, double depthSlope, double northRate, double eastRate, double yawRate,
			double slipSpeed, bool friction, bool record, ref double load, ref double forceNorth, ref double forceEast, ref double moment)
		{
			if (!(t1 > t0))
			{
				return;
			}

			var touched = false;
			var span = t1 - t0;
			var contactLength = span * _edgeLength[e];
			var samples = Math.Max(1, (int)Math.Ceiling(contactLength / SampleSpacing));
			var weight = contactLength / samples;
			for (var m = 0; m < samples; m++)
			{
				var t = t0 + (m + 0.5) * span / samples;
				var depth = depthA + t * depthSlope;
				var pointNorth = _aNorth + t * (_bNorth - _aNorth);
				var pointEast = _aEast + t * (_bEast - _aEast);
				var velocityNorth = northRate - yawRate * pointEast;
				var velocityEast = eastRate + yawRate * pointNorth;
				var directionNorth = _directionNorth[j];
				var directionEast = _directionEast[j];
				var compressionRate = velocityEast * directionNorth - velocityNorth * directionEast;
				var elastic = _law[j].Force(depth);
				var normal = (elastic + _law[j].Damping(depth) * compressionRate) * weight;
				var hysteresis = _law[j].Hysteresis;
				if (UseHysteresis && hysteresis > 0.0)
				{
					normal += hysteresis * elastic * weight * compressionRate / Math.Sqrt(compressionRate * compressionRate + slipSpeed * slipSpeed);
				}

				if (!(normal > 0.0))
				{
					continue;
				}

				var coefficient = _law[j].Friction;
				load += coefficient * normal;
				if (UseHysteresis && hysteresis > 0.0)
				{
					load += hysteresis * elastic * weight;
				}

				var tangential = 0.0;
				if (friction)
				{
					var slip = velocityNorth * directionNorth + velocityEast * directionEast;
					tangential = -coefficient * normal * slip / Math.Sqrt(slip * slip + slipSpeed * slipSpeed);
				}

				var pointForceNorth = normal * directionEast + tangential * directionNorth;
				var pointForceEast = tangential * directionEast - normal * directionNorth;
				forceNorth += pointForceNorth;
				forceEast += pointForceEast;
				moment += pointNorth * pointForceEast - pointEast * pointForceNorth;
				if (record)
				{
					_normalSum[j] += normal;
					_frictionSum[j] += tangential;
					_lengthSum[j] += weight;
					touched = true;
				}
			}

			if (touched)
			{
				// The depth is linear along the edge, so the deepest point is at one end of the part.
				_deepest[j] = Math.Max(_deepest[j], Math.Max(depthA + t0 * depthSlope, depthA + t1 * depthSlope));
			}
		}

		// Sets edge e's ends relative to the reference point at this pose.
		private void Edge(int e, double north, double east, double cos, double sin)
		{
			_originNorth = north;
			_originEast = east;
			_aNorth = _edgeStartX[e] * cos - _edgeStartY[e] * sin;
			_aEast = _edgeStartX[e] * sin + _edgeStartY[e] * cos;
			_bNorth = _edgeEndX[e] * cos - _edgeEndY[e] * sin;
			_bEast = _edgeEndX[e] * sin + _edgeEndY[e] * cos;
		}

		// Fills _owner and _entered for the current edge's cells: the face each is in contact with (unless
		// fresh is set) and, for the others, the face it entered since the last commit, if any: crossed from the
		// water side within the segment's length. A cell that entered several takes the one more of it entered.
		// True when any cell has a face. Left unfilled when the edge has no cell in contact and none entered.
		private bool Owners(int e, bool moved, bool fresh)
		{
			var cells = _edgeCells[e];
			var filled = false;
			var any = false;
			if (_edgeInContact[e] > 0)
			{
				Fill(e, fresh);
				filled = true;
				any = !fresh;
			}

			if (!moved)
			{
				return any;
			}

			var fromANorth = _fromNorth + _edgeStartX[e] * _fromCos - _edgeStartY[e] * _fromSin;
			var fromAEast = _fromEast + _edgeStartX[e] * _fromSin + _edgeStartY[e] * _fromCos;
			var fromBNorth = _fromNorth + _edgeEndX[e] * _fromCos - _edgeEndY[e] * _fromSin;
			var fromBEast = _fromEast + _edgeEndX[e] * _fromSin + _edgeEndY[e] * _fromCos;
			for (var i = 0; i < _activeCount; i++)
			{
				var j = _active[i];
				Locate(j, fromANorth, fromAEast, out _, out var fromDepthA);
				Locate(j, fromBNorth, fromBEast, out _, out var fromDepthB);
				Locate(j, _originNorth + _aNorth, _originEast + _aEast, out var alongA, out var depthA);
				Locate(j, _originNorth + _bNorth, _originEast + _bEast, out var alongB, out var depthB);
				var length = _segmentLength[j];
				var t0 = 0.0;
				var t1 = 1.0;
				if (!Clip(-fromDepthA, -fromDepthB, ref t0, ref t1) ||
					!Clip(depthA, depthB, ref t0, ref t1) ||
					!Clip(alongA, alongB, ref t0, ref t1) ||
					!Clip(length - alongA, length - alongB, ref t0, ref t1))
				{
					continue;
				}

				if (!filled)
				{
					Fill(e, fresh);
					filled = true;
				}

				var last = Math.Min(cells - 1, (int)Math.Ceiling(t1 * cells) - 1);
				for (var k = (int)(t0 * cells); k <= last; k++)
				{
					var overlap = Overlap(k / (double)cells, (k + 1) / (double)cells, t0, t1);
					if (overlap > _entered[k])
					{
						_owner[k] = j;
						_entered[k] = overlap;
						any = true;
					}
				}
			}

			return any;
		}

		// Starts _owner and _entered for edge e's cells from their contact (none if fresh is set).
		private void Fill(int e, bool fresh)
		{
			var first = _edgeFirstCell[e];
			for (var k = 0; k < _edgeCells[e]; k++)
			{
				var segment = fresh ? -1 : _cellSegment[first + k];
				_owner[k] = segment;
				_entered[k] = segment >= 0 ? double.PositiveInfinity : 0.0;
			}
		}

		// Where the part [ta, tb] of the current edge, in contact with segment j, touches it and its neighbours.
		private void Find(int j, double ta, double tb, ref Regions r)
		{
			Locate(j, _originNorth + _aNorth, _originEast + _aEast, out var alongA, out var depthA);
			Locate(j, _originNorth + _bNorth, _originEast + _bEast, out var alongB, out var depthB);
			var length = _segmentLength[j];
			r.DepthA = depthA;
			r.DepthSlope = depthB - depthA;
			r.Next = -1;
			r.Previous = -1;
			r.Start = ta;
			r.End = tb;
			if (!Clip(depthA, depthB, ref r.Start, ref r.End) ||
				!Clip(alongA, alongB, ref r.Start, ref r.End) ||
				!Clip(length - alongA, length - alongB, ref r.Start, ref r.End))
			{
				r.Start = 0.0;
				r.End = 0.0;
			}

			// Past j's end, and before its start.
			var s0 = ta;
			var s1 = tb;
			if (Clip(alongA - length, alongB - length, ref s0, ref s1))
			{
				Beside(j, Neighbour(j, true), true, depthA, depthB, s0, s1, out r.Next, out r.NextStart, out r.NextEnd, out r.NextDepthA, out r.NextDepthSlope,
					out r.EndCornerStart, out r.EndCornerEnd);
			}

			s0 = ta;
			s1 = tb;
			if (Clip(-alongA, -alongB, ref s0, ref s1))
			{
				Beside(j, Neighbour(j, false), false, depthA, depthB, s0, s1, out r.Previous, out r.PreviousStart, out r.PreviousEnd, out r.PreviousDepthA,
					out r.PreviousDepthSlope, out r.StartCornerStart, out r.StartCornerEnd);
			}
		}

		// The part [s0, s1] of the current edge lies past segment j's end (atEnd) or before its start; k is the
		// segment joining there. Gives k's part: behind k within its length. And the inside corner: behind both,
		// on k's far side of the joint, which stays with j.
		private void Beside(int j, int k, bool atEnd, double depthA, double depthB, double s0, double s1, out int neighbour, out double start, out double end,
			out double neighbourDepthA, out double neighbourDepthSlope, out double cornerStart, out double cornerEnd)
		{
			neighbour = -1;
			start = 0.0;
			end = 0.0;
			neighbourDepthA = 0.0;
			neighbourDepthSlope = 0.0;
			cornerStart = 0.0;
			cornerEnd = 0.0;
			if (k < 0)
			{
				return;
			}

			Locate(k, _originNorth + _aNorth, _originEast + _aEast, out var alongA, out var kDepthA);
			Locate(k, _originNorth + _bNorth, _originEast + _bEast, out var alongB, out var kDepthB);
			var length = _segmentLength[k];
			var t0 = s0;
			var t1 = s1;
			if (Clip(kDepthA, kDepthB, ref t0, ref t1) && Clip(alongA, alongB, ref t0, ref t1) && Clip(length - alongA, length - alongB, ref t0, ref t1))
			{
				neighbour = k;
				start = t0;
				end = t1;
				neighbourDepthA = kDepthA;
				neighbourDepthSlope = kDepthB - kDepthA;
			}

			t0 = s0;
			t1 = s1;
			var beyond = atEnd ? Clip(-alongA, -alongB, ref t0, ref t1) : Clip(alongA - length, alongB - length, ref t0, ref t1);
			if (beyond && Clip(depthA, depthB, ref t0, ref t1) && Clip(kDepthA, kDepthB, ref t0, ref t1))
			{
				cornerStart = t0;
				cornerEnd = t1;
			}
		}

		// The segment that starts where segment j ends (atEnd), or ends where it starts; −1 for none.
		private int Neighbour(int j, bool atEnd)
		{
			var known = atEnd ? _next : _previous;
			if (known[j] == Unknown)
			{
				known[j] = -1;
				var joint = atEnd ? _segment[j].End : _segment[j].Start;
				for (var i = 0; i < _segmentCount; i++)
				{
					var other = atEnd ? _segment[i].Start : _segment[i].End;
					if (i != j && Math.Abs(other.North - joint.North) <= JointTolerance && Math.Abs(other.East - joint.East) <= JointTolerance)
					{
						known[j] = i;
						break;
					}
				}
			}

			return known[j];
		}

		// Distance of a world point along prepared segment j from its start, and its depth behind the face.
		private void Locate(int j, double north, double east, out double along, out double depth)
		{
			var relativeNorth = north - _startNorth[j];
			var relativeEast = east - _startEast[j];
			along = relativeNorth * _directionNorth[j] + relativeEast * _directionEast[j];
			depth = relativeEast * _directionNorth[j] - relativeNorth * _directionEast[j];
		}

		// Narrows [t0, t1] to where a function going linearly from atStart (t = 0) to atEnd (t = 1) is not
		// negative. False when nothing is left.
		private static bool Clip(double atStart, double atEnd, ref double t0, ref double t1)
		{
			if (atStart < 0.0 && atEnd < 0.0)
			{
				return false;
			}

			if (atStart < 0.0)
			{
				t0 = Math.Max(t0, atStart / (atStart - atEnd));
			}
			else if (atEnd < 0.0)
			{
				t1 = Math.Min(t1, atStart / (atStart - atEnd));
			}

			return t0 < t1;
		}

		// Length of the overlap of [a0, a1] and [b0, b1]; not positive when they do not overlap.
		private static double Overlap(double a0, double a1, double b0, double b1) => Math.Min(a1, b1) - Math.Max(a0, b0);

		private static bool Same(in ContactSegment a, in ContactSegment b) => a.Id == b.Id && a.Start.Equals(b.Start) && a.End.Equals(b.End);

		private static void Point(OutlinePoint point, string path, out double x, out double y)
		{
			var section = Required.Section(point, path);
			x = Required.Value(section.X, path + ".x");
			y = Required.Value(section.Y, path + ".y");
		}

		// Where a run of cells along one edge touches its segment and the segments either side, as parameter
		// intervals along the edge (empty unless the start is below the end), with the depths behind each face.
		private struct Regions
		{
			// Behind the segment, within its length.
			public double Start;
			public double End;
			public double DepthA;
			public double DepthSlope;

			// Past its end: behind the next segment within that one's length, and the inside corner behind both.
			public int Next;
			public double NextStart;
			public double NextEnd;
			public double NextDepthA;
			public double NextDepthSlope;
			public double EndCornerStart;
			public double EndCornerEnd;

			// Before its start, the same with the previous segment.
			public int Previous;
			public double PreviousStart;
			public double PreviousEnd;
			public double PreviousDepthA;
			public double PreviousDepthSlope;
			public double StartCornerStart;
			public double StartCornerEnd;
		}
	}
}
