using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using Newtonsoft.Json.Linq;

namespace FerrySim.Slice
{
	/// <summary>A landing structure segment used for contact, with the pipeline's provenance and its fender.</summary>
	public sealed class LandingSegment
	{
		public LandingSegment(ContactSegment segment, int fileIndex, string stopId, string stopName, string source, string confidence, string verification,
			bool waterSideVerifiedByPipeline, bool waterSideCrewReported, bool isPrimary, LandingSurface surface, string kind = null, string berthId = null,
			bool solidSideVerified = false, bool crewBerthSolidSide = false)
		{
			Segment = segment;
			FileIndex = fileIndex;
			StopId = stopId;
			StopName = stopName;
			Source = source;
			Confidence = confidence;
			Verification = verification;
			WaterSideVerifiedByPipeline = waterSideVerifiedByPipeline;
			WaterSideCrewReported = waterSideCrewReported;
			IsPrimary = isPrimary;
			Surface = surface;
			Kind = kind;
			BerthId = berthId;
			SolidSideVerified = solidSideVerified;
			CrewBerthSolidSide = crewBerthSolidSide;
		}

		public ContactSegment Segment { get; }

		/// <summary>The pipeline's stable id, unchanged.</summary>
		public int Id => Segment.Id;

		/// <summary>Position in contacts.json, from 0.</summary>
		public int FileIndex { get; }

		public string StopId { get; }

		public string StopName { get; }

		/// <summary>How the pipeline found the face, for example <c>enc-feature</c> or <c>derived-from-gtfs</c>.</summary>
		public string Source { get; }

		/// <summary>The pipeline's confidence in the face.</summary>
		public string Confidence { get; }

		/// <summary>For example <c>needs-crew-verification</c>.</summary>
		public string Verification { get; }

		/// <summary>The pipeline's chart check confirmed which side is water (<c>water_side_verified_by_pipeline</c>).</summary>
		public bool WaterSideVerifiedByPipeline { get; }

		/// <summary>Crew confirmed which side is water (<c>water_side_crew_reported</c>), whatever the chart check said.</summary>
		public bool WaterSideCrewReported { get; }

		/// <summary>
		/// The segment belongs to a closed charted outline that is solid on its right, with charted water and no land or
		/// other structure just off its left (<c>solid_side_evidence.verified</c>, contacts.json version 2).
		/// </summary>
		public bool SolidSideVerified { get; }

		public bool CrewBerthSolidSide { get; }

		/// <summary>Collision retention alone does not authorize a start on this face subdivision.</summary>
		public bool StartFaceEligible => WaterSideVerifiedByPipeline || WaterSideCrewReported || CrewBerthSolidSide;

		/// <summary>
		/// Who vouches for the water side: <c>pipeline</c>, <c>crew</c> or <c>pipeline+crew</c>, or <c>solid-side</c> for a
		/// segment off the primary face kept on its charted solid side alone.
		/// </summary>
		public string WaterSideBasis => WaterSideVerifiedByPipeline ? (WaterSideCrewReported ? "pipeline+crew" : "pipeline") : WaterSideCrewReported ? "crew" : CrewBerthSolidSide ? "solid-side + crew berth use (water side unsurveyed: UNSARE)" : "solid-side";

		/// <summary>
		/// What the segment is (contacts.json version 2): <c>berth-face</c>, <c>barge-side</c>, <c>barge-end</c>, <c>pier</c>,
		/// <c>bulkhead</c> or <c>shoreline</c>; null in a version 1 file.
		/// </summary>
		public string Kind { get; }

		/// <summary>The berth the segment's barge outline is, for example <c>enc:0226015B0512B198</c>; null for fixed structures and version 1 files.</summary>
		public string BerthId { get; }

		/// <summary>Part of the stop's primary landing face, the one the pipeline selected (<c>primary_face</c>).</summary>
		public bool IsPrimary { get; }

		/// <summary>The fender entry the segment's <see cref="ContactSurface"/> came from.</summary>
		public LandingSurface Surface { get; }
	}

	/// <summary>A segment of contacts.json left out of the contact geometry, and why.</summary>
	public sealed class ExcludedLandingSegment
	{
		public ExcludedLandingSegment(int id, int fileIndex, string stopId, string stopName, bool isPrimary, string reason)
		{
			Id = id;
			FileIndex = fileIndex;
			StopId = stopId;
			StopName = stopName;
			IsPrimary = isPrimary;
			Reason = reason;
		}

		public int Id { get; }

		public int FileIndex { get; }

		public string StopId { get; }

		public string StopName { get; }

		/// <summary>The pipeline flags it as part of its stop's primary landing face (<c>primary_face</c>).</summary>
		public bool IsPrimary { get; }

		public string Reason { get; }

		public override string ToString() => $"segment {Id} (stop {StopId} {StopName}{(IsPrimary ? ", primary face" : string.Empty)}): {Reason}";
	}

	/// <summary>
	/// A stop's primary landing face, the one the pipeline selected (<c>primary_face</c>): its flagged segments in file
	/// order. The pipeline may split one side of a structure into several subsegments; they join end to start.
	/// </summary>
	public sealed class LandingFace
	{
		internal LandingFace(IReadOnlyList<LandingSegment> segments)
		{
			Segments = segments;
		}

		/// <summary>The face's segments in file order, at least one.</summary>
		public IReadOnlyList<LandingSegment> Segments { get; }

		public string StopId => Segments[0].StopId;

		public string StopName => Segments[0].StopName;

		/// <summary>Where the face starts: its first segment's start.</summary>
		public HorizontalVector Start => Segments[0].Segment.Start;

		/// <summary>Where the face ends: its last segment's end.</summary>
		public HorizontalVector End => Segments[Segments.Count - 1].Segment.End;
	}

	/// <summary>
	/// The landing structures the hull can touch, built at run time from the world importer's <c>contacts.json</c>
	/// (a list of segments: id, stop_id, stop_name, start_ne_m, end_ne_m, water_normal_ne, source, confidence,
	/// verification, water_side_verified_by_pipeline and the optional water_side_crew_reported and primary_face; version
	/// 2 adds the optional kind, berth_id and solid_side_evidence; other fields are ignored). A segment is used when the
	/// pipeline verified its water side or crew reported it, or, off the primary face only, when its charted solid side
	/// is verified, or the selectable crew-berth/UNSARE/AIS gate passes; and its normal agrees with Start to End. Every other segment is left out and listed in
	/// <see cref="Excluded"/>; segments kept on the crew's report alone are listed in <see cref="CrewReportedOnly"/> and
	/// those kept on their solid side alone in <see cref="SolidSideOnly"/>. Ids are kept as given. Fenders come from a
	/// <see cref="LandingSurfaceTable"/> by stop and kind, never from constants. A missing or malformed file throws
	/// <see cref="LandingDataException"/> naming the file and the reason.
	/// </summary>
	public sealed partial class LandingGeometry
	{
		/// <summary>The importer's file, relative to the Unity project.</summary>
		public const string DefaultContactsAssetPath = "Assets/FerrySim/World/Generated/LowerEastRiver/contacts.json";

		/// <summary>Largest angle between water_normal_ne and the left-hand normal of Start to End, degrees.</summary>
		public const double NormalToleranceDegrees = 1.0;

		/// <summary>How far water_normal_ne's length may be from 1 before the file counts as malformed.</summary>
		public const double UnitNormalTolerance = 1e-3;

		/// <summary>Shortest segment accepted, m: the core joins segment ends within 1 mm.</summary>
		public const double MinimumSegmentLength = 1e-3;

		/// <summary>Largest gap between one segment's end and the next one's start in a face, m: the core joins segment ends within 1 mm.</summary>
		public const double JointTolerance = 1e-3;

		public static readonly string[] RequiredFields =
		{
			"id", "stop_id", "stop_name", "start_ne_m", "end_ne_m", "water_normal_ne", "source", "confidence", "verification",
		};

		/// <summary>Booleans that vouch for the water side; either may be absent, which counts as false.</summary>
		public const string PipelineWaterSideField = "water_side_verified_by_pipeline";

		public const string CrewWaterSideField = "water_side_crew_reported";

		/// <summary>Marks the subsegments of the stop's primary landing face; absent counts as false.</summary>
		public const string PrimaryFaceField = "primary_face";

		/// <summary>Version 2, optional: what the segment is, which selects its fender (<see cref="LandingSurfaceTable.For(string, string)"/>).</summary>
		public const string KindField = "kind";

		/// <summary>Version 2, optional: the berth whose barge outline the segment belongs to.</summary>
		public const string BerthIdField = "berth_id";

		/// <summary>Version 2, optional: an object whose boolean <c>verified</c> vouches for the segment's charted solid side.</summary>
		public const string SolidSideField = "solid_side_evidence";

		private readonly Dictionary<int, LandingSegment> _byId = new Dictionary<int, LandingSegment>();

		private LandingGeometry()
		{
		}

		/// <summary>The contacts file, for messages.</summary>
		public string ContactsFile { get; private set; }

		/// <summary>SHA-256 of the contacts file's text (UTF-8), lowercase hex.</summary>
		public string ContactsSha256 { get; private set; }

		public LandingSurfaceTable Surfaces { get; private set; }

		/// <summary>The kept segments in file order, ready for <c>VesselSimulationRunner.WorldGeometry</c>.</summary>
		public SegmentListGeometry Geometry { get; private set; }

		/// <summary>The kept segments, in the same order as <see cref="Geometry"/>.</summary>
		public IReadOnlyList<LandingSegment> Segments { get; private set; }

		public IReadOnlyList<ExcludedLandingSegment> Excluded { get; private set; }

		/// <summary>Kept segments whose water side only crew vouch for: the pipeline's chart check failed or is missing.</summary>
		public IReadOnlyList<LandingSegment> CrewReportedOnly { get; private set; }

		/// <summary>Kept without water-side proof, on solid-side geometry (including the distinct crew-berth gate).</summary>
		public IReadOnlyList<LandingSegment> SolidSideOnly { get; private set; }

		/// <summary>Segments in the file, kept or not.</summary>
		public int SegmentsRead { get; private set; }

		/// <summary>The geometry's <see cref="WorldGeometryDigest"/>, as recordings carry it.</summary>
		public string Digest => WorldGeometryDigest.Of(Geometry);

		/// <summary>The kept segment with this id, or null.</summary>
		public LandingSegment Find(int id) => _byId.TryGetValue(id, out var segment) ? segment : null;

		/// <summary>The kept segments of one landing, in file order.</summary>
		public IReadOnlyList<LandingSegment> ForStop(string stopId)
		{
			var found = new List<LandingSegment>();
			foreach (var segment in Segments)
			{
				if (segment.StopId == stopId)
				{
					found.Add(segment);
				}
			}

			return found;
		}

		/// <summary>
		/// The stop's primary landing face, the one the pipeline selected: every segment of the stop flagged
		/// <c>primary_face</c>, in file order. They must all be kept and join end to start (within
		/// <see cref="JointTolerance"/>); otherwise this throws <see cref="LandingDataException"/> naming the file. It
		/// never falls back to another face of the stop: the stop's first usable segment may be another side of the
		/// structure.
		/// </summary>
		public LandingFace PrimaryFace(string stopId)
		{
			var problems = new List<string>();
			foreach (var excluded in Excluded)
			{
				if (excluded.StopId == stopId && excluded.IsPrimary)
				{
					problems.Add($"stop {stopId}: primary face segment {excluded.Id} is left out of contact ({excluded.Reason})");
				}
			}

			var face = new List<LandingSegment>();
			foreach (var segment in Segments)
			{
				if (segment.StopId != stopId || !segment.IsPrimary)
				{
					continue;
				}

				if (face.Count > 0)
				{
					var previous = face[face.Count - 1];
					var gap = (segment.Segment.Start - previous.Segment.End).Magnitude;
					if (!(gap <= JointTolerance))
					{
						problems.Add($"stop {stopId}: primary face segments {previous.Id} and {segment.Id} do not join end to start " +
							$"({gap.ToString("G4", CultureInfo.InvariantCulture)} m apart), so they are not one face");
					}
				}

				face.Add(segment);
			}

			if (face.Count == 0 && problems.Count == 0)
			{
				problems.Add($"stop {stopId}: no segment is flagged {PrimaryFaceField}, so its landing face is unknown");
			}

			if (problems.Count > 0)
			{
				throw new LandingDataException(ContactsFile, problems);
			}

			return new LandingFace(face);
		}

		/// <summary>What was read, kept and left out, one line per segment, for the log and the handoff.</summary>
		public string Describe()
		{
			var invariant = CultureInfo.InvariantCulture;
			var text = new StringBuilder();
			text.Append("Landing contact geometry from ").Append(ContactsFile).Append(" (sha256 ").Append(ContactsSha256).Append("): ")
				.Append(SegmentsRead).Append(" segments read, ").Append(Segments.Count).Append(" kept, ").Append(Excluded.Count).Append(" left out.\n");
			text.Append("Fenders from ").Append(Surfaces.File).Append(".\n");
			text.Append("Digest ").Append(Digest).Append('\n');
			text.Append("Kept:\n");
			foreach (var segment in Segments)
			{
				var surface = segment.Surface.Surface;
				text.AppendFormat(invariant, "  {0} stop {1} {2}{3}{15}: {4:F1} m, {5}/{6}, {7}, water side: {8}; fender '{9}' ({10}) k {11:G4} N/m per m, c {12:G4} N*s/m per m, mu {13:G3}, travel {14:G3} m\n",
					segment.Id, segment.StopId, segment.StopName, segment.IsPrimary ? " (primary face)" : string.Empty, segment.Segment.Length, segment.Source, segment.Confidence,
					segment.Verification, segment.WaterSideBasis, segment.Surface.Key, segment.Surface.Confidence, surface.Stiffness, surface.Damping, surface.Friction, surface.MaxCompression,
					segment.Kind == null ? string.Empty : " " + segment.Kind + (segment.BerthId == null ? string.Empty : " of " + segment.BerthId));
			}

			text.Append(SolidSideOnly.Count == 0 ? "Kept without water-side proof on charted solid geometry: none\n" : "Kept without water-side proof on charted solid geometry:\n");
			foreach (var segment in SolidSideOnly)
			{
				text.AppendFormat(invariant, "  {0} stop {1} {2} {3}\n", segment.Id, segment.StopId, segment.StopName, segment.Kind);
			}

			text.Append(CrewReportedOnly.Count == 0 ? "Kept on the crew's report only (pipeline chart check failed or missing): none\n" : "Kept on the crew's report only (pipeline chart check failed or missing):\n");
			foreach (var segment in CrewReportedOnly)
			{
				text.AppendFormat(invariant, "  {0} stop {1} {2}\n", segment.Id, segment.StopId, segment.StopName);
			}

			text.Append(Excluded.Count == 0 ? "Left out: none\n" : "Left out:\n");
			foreach (var excluded in Excluded)
			{
				text.Append("  ").Append(excluded).Append('\n');
			}

			return text.ToString();
		}

		/// <summary>Builds the geometry from contacts.json text; <paramref name="contactsFile"/> names it in errors.</summary>
		public static LandingGeometry Parse(string json, string contactsFile, LandingSurfaceTable surfaces)
		{
			if (surfaces == null)
			{
				throw new ArgumentNullException(nameof(surfaces));
			}

			var root = Json.ParseRoot(json, contactsFile) as JArray ??
				throw new LandingDataException(contactsFile, new[] { "expected a JSON array of landing face segments" });
			var problems = new List<string>();
			var kept = new List<LandingSegment>();
			var excluded = new List<ExcludedLandingSegment>();
			var seen = new Dictionary<int, int>();
			for (var index = 0; index < root.Count; index++)
			{
				ReadSegment(root[index], index, surfaces, problems, seen, kept, excluded);
			}

			if (problems.Count > 0)
			{
				throw new LandingDataException(contactsFile, problems);
			}

			var segments = new ContactSegment[kept.Count];
			for (var i = 0; i < kept.Count; i++)
			{
				segments[i] = kept[i].Segment;
			}

			var geometry = new LandingGeometry
			{
				ContactsFile = contactsFile,
				ContactsSha256 = Sha256(json),
				Surfaces = surfaces,
				Geometry = new SegmentListGeometry(segments),
				Segments = kept,
				Excluded = excluded,
				CrewReportedOnly = kept.FindAll(segment => !segment.WaterSideVerifiedByPipeline && segment.WaterSideCrewReported),
				SolidSideOnly = kept.FindAll(segment => !segment.WaterSideVerifiedByPipeline && !segment.WaterSideCrewReported),
				SegmentsRead = root.Count,
			};
			foreach (var segment in kept)
			{
				geometry._byId[segment.Id] = segment;
			}

			geometry.ReadBerths(root);
			return geometry;
		}

		/// <summary>Reads both files from disk.</summary>
		public static LandingGeometry Load(string contactsPath, string surfacesPath)
		{
			var surfaces = LandingSurfaceTable.Load(surfacesPath);
			return Parse(Json.ReadFile(contactsPath), contactsPath, surfaces);
		}

		private static void ReadSegment(JToken token, int index, LandingSurfaceTable surfaces, List<string> problems, Dictionary<int, int> seen,
			List<LandingSegment> kept, List<ExcludedLandingSegment> excluded)
		{
			if (!(token is JObject segment))
			{
				problems.Add($"segment {index}: must be an object");
				return;
			}

			var hasId = TryId(segment["id"], out var id);
			var where = $"segment {index} (id {(hasId ? id.ToString(CultureInfo.InvariantCulture) : "?")})";
			var before = problems.Count;
			foreach (var field in RequiredFields)
			{
				if (segment[field] == null)
				{
					problems.Add($"{where}: {field} missing");
				}
			}

			if (problems.Count > before)
			{
				return;
			}

			if (!hasId)
			{
				problems.Add($"{where}: id must be a whole number from 1 to {int.MaxValue}");
			}
			else if (seen.TryGetValue(id, out var first))
			{
				problems.Add($"{where}: duplicate id {id} (also segment {first}); ids must be stable and unique");
			}
			else
			{
				seen[id] = index;
			}

			var stopId = Text(segment, "stop_id", where, problems, nonEmpty: true);
			var stopName = Text(segment, "stop_name", where, problems, nonEmpty: false);
			var source = Text(segment, "source", where, problems, nonEmpty: true);
			var confidence = Text(segment, "confidence", where, problems, nonEmpty: true);
			var verification = Text(segment, "verification", where, problems, nonEmpty: true);
			var hasStart = TryPoint(segment["start_ne_m"], out var start);
			var hasEnd = TryPoint(segment["end_ne_m"], out var end);
			var hasNormal = TryPoint(segment["water_normal_ne"], out var normal);
			if (!hasStart)
			{
				problems.Add($"{where}: start_ne_m must be [north, east], two finite numbers in metres");
			}

			if (!hasEnd)
			{
				problems.Add($"{where}: end_ne_m must be [north, east], two finite numbers in metres");
			}

			if (!hasNormal)
			{
				problems.Add($"{where}: water_normal_ne must be [north, east], two finite numbers");
			}
			else if (Math.Abs(normal.Magnitude - 1.0) > UnitNormalTolerance)
			{
				problems.Add($"{where}: water_normal_ne must be a unit vector, length is {normal.Magnitude.ToString("G6", CultureInfo.InvariantCulture)}");
			}

			var verified = Flag(segment, PipelineWaterSideField, where, problems);
			var crew = Flag(segment, CrewWaterSideField, where, problems);
			var primary = Flag(segment, PrimaryFaceField, where, problems);
			var kind = OptionalText(segment, KindField, where, problems, nonEmpty: true);
			var berthId = OptionalText(segment, BerthIdField, where, problems, nonEmpty: false);
			var solidSide = SolidSide(segment, where, problems);
			var berthSolid = BerthSolidContact(segment);

			if (hasStart && hasEnd)
			{
				// Finite end points can still be too far apart for a finite difference or length; the core's segment
				// would then refuse them with an exception that names no file.
				var length = (end - start).Magnitude;
				if (double.IsNaN(length) || double.IsInfinity(length))
				{
					problems.Add($"{where}: start_ne_m to end_ne_m is not a finite length; the points are too far apart for the world frame");
				}
				else if (length < MinimumSegmentLength)
				{
					problems.Add($"{where}: shorter than {MinimumSegmentLength.ToString(CultureInfo.InvariantCulture)} m; start_ne_m and end_ne_m must be distinct");
				}
			}

			if (problems.Count > before)
			{
				return;
			}

			if (!verified && !crew && !berthSolid && (primary || !solidSide))
			{
				var solidText = segment[SolidSideField] == null ? "absent" : solidSide ? "true" : "false";
				excluded.Add(new ExcludedLandingSegment(id, index, stopId, stopName, primary,
					$"water side neither verified by the pipeline ({PipelineWaterSideField} {FlagText(segment[PipelineWaterSideField])}) nor reported by crew ({CrewWaterSideField} {FlagText(segment[CrewWaterSideField])})" +
					(primary ? (solidSide ? ", and a primary face is never kept on its solid side alone" : string.Empty) : $", nor its charted solid side verified ({SolidSideField}.verified {solidText})")));
				return;
			}

			var surface = surfaces.For(stopId, kind);
			ContactSegment contact;
			try
			{
				contact = new ContactSegment(start, end, surface.Surface, id);
			}
			catch (ArgumentException exception)
			{
				// The core's own contract check; the checks above should already have named the problem.
				problems.Add($"{where}: {exception.Message}");
				return;
			}

			var computed = contact.WaterNormal;
			var cos = (computed.North * normal.North + computed.East * normal.East) / normal.Magnitude;
			var angle = Math.Acos(Math.Max(-1.0, Math.Min(1.0, cos))) * 180.0 / Math.PI;
			if (angle > NormalToleranceDegrees)
			{
				excluded.Add(new ExcludedLandingSegment(id, index, stopId, stopName, primary,
					$"water_normal_ne is {angle.ToString("0.#", CultureInfo.InvariantCulture)}° from the left-hand normal of start to end (tolerance {NormalToleranceDegrees.ToString(CultureInfo.InvariantCulture)}°), so its solid side is uncertain"));
				return;
			}

			kept.Add(new LandingSegment(contact, index, stopId, stopName, source, confidence, verification, verified, crew, primary, surface, kind, berthId, solidSide, berthSolid));
		}

		// Same narrow version-2 gate as export_unity. The pipeline validates the embedded note against
		// the current manifest and independently recomputes probes/counts; players carry that export.
		private static bool BerthSolidContact(JObject segment)
		{
			bool Yes(JToken token) => token?.Type == JTokenType.Boolean && (bool)token;
			bool No(JToken token) => token?.Type == JTokenType.Boolean && !(bool)token;
			string Text(JToken token) => token?.Type == JTokenType.String ? (string)token : null;
			var use = segment["berth_use_evidence"] as JObject;
			var note = use?["note"] as JObject;
			var solid = segment["solid_side_evidence"] as JObject;
			var side = segment["berth_side_evidence"] as JObject;
			if (!Yes(segment["berth_face"]) || note == null || solid == null || side == null) return false;
			var noteId = note["id"]?.Type == JTokenType.String ? (string)note["id"] : null;
			if (string.IsNullOrWhiteSpace(noteId) || Text(segment["berth_use_crew_note"]) != noteId || Text(note["kind"]) != "berth-use-policy"
				|| use["crew_notes_sha256"]?.Type != JTokenType.String || ((string)use["crew_notes_sha256"]).Length != 64) return false;
			var listed = false;
			if (note["berths"] is JArray berths)
				foreach (var berth in berths.OfType<JObject>()) if (Text(berth["id"]) != null && Text(berth["id"]) == Text(segment["berth_id"])) listed = true;
			var outward = side["outward_reports"]; var inward = side["inward_reports"];
			var offset = solid["probe_offset_m"];
			if (offset?.Type != JTokenType.Float && offset?.Type != JTokenType.Integer) return false;
			if (!long.TryParse(outward?.ToString(), out var outCount) || !long.TryParse(inward?.ToString(), out var inCount)) return false;
			return listed && Yes(solid["verified"]) && Yes(solid["winding_check"]) && (double)offset == 0.5
				&& Yes(solid["probe_in_charted_water"]) && Text(solid["probe_water_layer"]) == "UNSARE"
				&& No(solid["probe_in_lndare"]) && No(solid["probe_in_other_structure"]) && Yes(side["verified"])
				&& outward?.Type == JTokenType.Integer && inward?.Type == JTokenType.Integer
				&& outCount > 0 && inCount >= 0 && inCount <= outCount;
		}

		/// <summary>An optional text field: absent or null is null; anything but text (or empty text when it must not be) is malformed.</summary>
		private static string OptionalText(JObject segment, string field, string where, List<string> problems, bool nonEmpty)
		{
			var token = segment[field];
			if (token == null || token.Type == JTokenType.Null)
			{
				return null;
			}

			if (token.Type != JTokenType.String || (nonEmpty && string.IsNullOrWhiteSpace((string)token)))
			{
				problems.Add($"{where}: {field} must be {(nonEmpty ? "non-empty " : string.Empty)}text");
				return null;
			}

			return (string)token;
		}

		/// <summary>The optional solid-side proof: absent is false; an object whose verified is not true or false is malformed.</summary>
		private static bool SolidSide(JObject segment, string where, List<string> problems)
		{
			var token = segment[SolidSideField];
			if (token == null || token.Type == JTokenType.Null)
			{
				return false;
			}

			if (!(token is JObject evidence))
			{
				problems.Add($"{where}: {SolidSideField} must be an object");
				return false;
			}

			var verified = evidence["verified"];
			if (verified == null || verified.Type != JTokenType.Boolean)
			{
				problems.Add($"{where}: {SolidSideField}.verified must be true or false");
				return false;
			}

			return (bool)verified;
		}

		/// <summary>An optional boolean: absent or null is false; anything but true or false is malformed.</summary>
		private static bool Flag(JObject segment, string field, string where, List<string> problems)
		{
			var token = segment[field];
			if (token == null || token.Type == JTokenType.Null)
			{
				return false;
			}

			if (token.Type != JTokenType.Boolean)
			{
				problems.Add($"{where}: {field} must be true or false");
				return false;
			}

			return (bool)token;
		}

		private static string FlagText(JToken flag) => flag == null || flag.Type == JTokenType.Null ? "absent" : (bool)flag ? "true" : "false";

		private static bool TryId(JToken token, out int id)
		{
			id = 0;
			if (!(token is JValue value) || value.Type != JTokenType.Integer)
			{
				return false;
			}

			// Range-check the value as read: an integer past Int64 arrives as a BigInteger, and casting it to long throws.
			switch (value.Value)
			{
				case long whole when whole >= 1 && whole <= int.MaxValue:
					id = (int)whole;
					return true;
				case int whole when whole >= 1:
					id = whole;
					return true;
				default:
					return false;
			}
		}

		private static bool TryPoint(JToken token, out HorizontalVector point)
		{
			point = default;
			if (!(token is JArray array) || array.Count != 2 || !Json.TryNumber(array[0], out var north) || !Json.TryNumber(array[1], out var east))
			{
				return false;
			}

			point = new HorizontalVector(north, east);
			return true;
		}

		private static string Text(JObject segment, string field, string where, List<string> problems, bool nonEmpty)
		{
			var token = segment[field];
			if (token.Type != JTokenType.String || (nonEmpty && string.IsNullOrWhiteSpace((string)token)))
			{
				problems.Add($"{where}: {field} must be {(nonEmpty ? "non-empty " : string.Empty)}text");
				return null;
			}

			return (string)token;
		}

		private static string Sha256(string text)
		{
			using (var sha = SHA256.Create())
			{
				var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
				var hex = new StringBuilder(hash.Length * 2);
				foreach (var b in hash)
				{
					hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
				}

				return hex.ToString();
			}
		}
	}
}
