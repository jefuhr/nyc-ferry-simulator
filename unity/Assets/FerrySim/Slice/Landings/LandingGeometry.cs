using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FerrySim.Core.Contracts;
using FerrySim.Sim;
using Newtonsoft.Json.Linq;

namespace FerrySim.Slice
{
	/// <summary>A landing face segment used for contact, with the pipeline's provenance and its fender.</summary>
	public sealed class LandingSegment
	{
		public LandingSegment(ContactSegment segment, int fileIndex, string stopId, string stopName, string source, string confidence, string verification,
			bool waterSideVerifiedByPipeline, bool waterSideCrewReported, LandingSurface surface)
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
			Surface = surface;
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

		/// <summary>Who vouches for the water side: <c>pipeline</c>, <c>crew</c> or <c>pipeline+crew</c>.</summary>
		public string WaterSideBasis => WaterSideVerifiedByPipeline ? (WaterSideCrewReported ? "pipeline+crew" : "pipeline") : "crew";

		/// <summary>The fender entry the segment's <see cref="ContactSurface"/> came from.</summary>
		public LandingSurface Surface { get; }
	}

	/// <summary>A segment of contacts.json left out of the contact geometry, and why.</summary>
	public sealed class ExcludedLandingSegment
	{
		public ExcludedLandingSegment(int id, int fileIndex, string stopId, string stopName, string reason)
		{
			Id = id;
			FileIndex = fileIndex;
			StopId = stopId;
			StopName = stopName;
			Reason = reason;
		}

		public int Id { get; }

		public int FileIndex { get; }

		public string StopId { get; }

		public string StopName { get; }

		public string Reason { get; }

		public override string ToString() => $"segment {Id} (stop {StopId} {StopName}): {Reason}";
	}

	/// <summary>
	/// The landing faces the hull can touch, built at run time from the world importer's <c>contacts.json</c>
	/// (a list of segments: id, stop_id, stop_name, start_ne_m, end_ne_m, water_normal_ne, source, confidence,
	/// verification, water_side_verified_by_pipeline and the optional water_side_crew_reported; other fields are
	/// ignored). A segment is used when the pipeline verified its water side or crew reported it, and its normal
	/// agrees with Start to End; every other segment is left out and listed in <see cref="Excluded"/>, and segments
	/// kept on the crew's report alone are listed in <see cref="CrewReportedOnly"/>. Ids are kept as given. Fenders
	/// come from a <see cref="LandingSurfaceTable"/>, never from constants. A missing or malformed file throws
	/// <see cref="LandingDataException"/> naming the file and the reason.
	/// </summary>
	public sealed class LandingGeometry
	{
		/// <summary>The importer's file, relative to the Unity project.</summary>
		public const string DefaultContactsAssetPath = "Assets/FerrySim/World/Generated/LowerEastRiver/contacts.json";

		/// <summary>Largest angle between water_normal_ne and the left-hand normal of Start to End, degrees.</summary>
		public const double NormalToleranceDegrees = 1.0;

		/// <summary>How far water_normal_ne's length may be from 1 before the file counts as malformed.</summary>
		public const double UnitNormalTolerance = 1e-3;

		/// <summary>Shortest segment accepted, m: the core joins segment ends within 1 mm.</summary>
		public const double MinimumSegmentLength = 1e-3;

		public static readonly string[] RequiredFields =
		{
			"id", "stop_id", "stop_name", "start_ne_m", "end_ne_m", "water_normal_ne", "source", "confidence", "verification",
		};

		/// <summary>Booleans that vouch for the water side; either may be absent, which counts as false.</summary>
		public const string PipelineWaterSideField = "water_side_verified_by_pipeline";

		public const string CrewWaterSideField = "water_side_crew_reported";

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
				text.AppendFormat(invariant, "  {0} stop {1} {2}: {3:F1} m, {4}/{5}, {6}, water side: {7}; fender '{8}' ({9}) k {10:G4} N/m per m, c {11:G4} N*s/m per m, mu {12:G3}, travel {13:G3} m\n",
					segment.Id, segment.StopId, segment.StopName, segment.Segment.Length, segment.Source, segment.Confidence, segment.Verification, segment.WaterSideBasis,
					segment.Surface.Key, segment.Surface.Confidence, surface.Stiffness, surface.Damping, surface.Friction, surface.MaxCompression);
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
				CrewReportedOnly = kept.FindAll(segment => !segment.WaterSideVerifiedByPipeline),
				SegmentsRead = root.Count,
			};
			foreach (var segment in kept)
			{
				geometry._byId[segment.Id] = segment;
			}

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

			if (hasStart && hasEnd && (end - start).Magnitude < MinimumSegmentLength)
			{
				problems.Add($"{where}: shorter than {MinimumSegmentLength.ToString(CultureInfo.InvariantCulture)} m; start_ne_m and end_ne_m must be distinct");
			}

			if (problems.Count > before)
			{
				return;
			}

			if (!verified && !crew)
			{
				excluded.Add(new ExcludedLandingSegment(id, index, stopId, stopName,
					$"water side neither verified by the pipeline ({PipelineWaterSideField} {FlagText(segment[PipelineWaterSideField])}) nor reported by crew ({CrewWaterSideField} {FlagText(segment[CrewWaterSideField])})"));
				return;
			}

			var surface = surfaces.For(stopId);
			var contact = new ContactSegment(start, end, surface.Surface, id);
			var computed = contact.WaterNormal;
			var cos = (computed.North * normal.North + computed.East * normal.East) / normal.Magnitude;
			var angle = Math.Acos(Math.Max(-1.0, Math.Min(1.0, cos))) * 180.0 / Math.PI;
			if (angle > NormalToleranceDegrees)
			{
				excluded.Add(new ExcludedLandingSegment(id, index, stopId, stopName,
					$"water_normal_ne is {angle.ToString("0.#", CultureInfo.InvariantCulture)}° from the left-hand normal of start to end (tolerance {NormalToleranceDegrees.ToString(CultureInfo.InvariantCulture)}°), so its solid side is uncertain"));
				return;
			}

			kept.Add(new LandingSegment(contact, index, stopId, stopName, source, confidence, verification, verified, crew, surface));
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
			if (token == null || token.Type != JTokenType.Integer)
			{
				return false;
			}

			var value = (long)token;
			if (value < 1 || value > int.MaxValue)
			{
				return false;
			}

			id = (int)value;
			return true;
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
