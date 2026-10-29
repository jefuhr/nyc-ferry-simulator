using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace FerrySim.Harbor
{
	/// <summary>
	/// The harbor landing audit as the composition root ships it: <c>Harbor/Data/landing-audit.json</c>, written from
	/// <c>python -m ferrydata.harbor_landings matrix</c> on the adopted harbor packs (data-pipeline/ferrydata/harbor_landings/README.md).
	/// Each berth that has a start pose becomes a <see cref="HarborBerthMark"/> whose note says how many of the sampled
	/// starts were afloat; a berth without a start pose gets no mark, so the catalog's own crew-answer note stands. The
	/// audit refuses nothing: aground starts are still refused per tide by the catalog's depth check.
	/// </summary>
	public sealed class HarborLandingAudit
	{
		public const string Schema = "ferrysim-harbor-landing-audit/1";

		private HarborLandingAudit(string depthDigest, int rowsPerHull, IReadOnlyList<AuditBerth> berths, IReadOnlyList<HarborBerthMark> marks)
		{
			DepthDigest = depthDigest;
			RowsPerHull = rowsPerHull;
			Berths = berths;
			Marks = marks;
		}

		/// <summary>The verified depth set the matrix was assessed on; must equal the runtime pack's depth linkage.</summary>
		public string DepthDigest { get; }

		/// <summary>Sampled starts per berth and hull (28 days × 25 samples).</summary>
		public int RowsPerHull { get; }

		public IReadOnlyList<AuditBerth> Berths { get; }

		/// <summary>One mark per berth with a start pose, for <see cref="HarborScenarioCatalog"/>.</summary>
		public IReadOnlyList<HarborBerthMark> Marks { get; }

		public static HarborLandingAudit Parse(string json, string file)
		{
			JObject root;
			try
			{
				root = JObject.Parse(json ?? throw new ArgumentNullException(nameof(json)));
			}
			catch (Newtonsoft.Json.JsonException error)
			{
				throw new FormatException(file + ": " + error.Message);
			}

			if ((string)root["schema"] != Schema)
			{
				throw new FormatException(file + ": schema is '" + root["schema"] + "', expected '" + Schema + "'.");
			}

			var digest = (string)root["depth_digest"];
			if (digest == null || digest.Length != 64)
			{
				throw new FormatException(file + ": depth_digest must be the depth set's SHA-256.");
			}

			if (root["rows_per_hull"] == null || root["rows_per_hull"].Type != JTokenType.Integer || (int)root["rows_per_hull"] <= 0)
			{
				throw new FormatException(file + ": rows_per_hull must be a positive count.");
			}

			var rowsPerHull = (int)root["rows_per_hull"];
			if (!(root["berths"] is JArray berthTokens) || berthTokens.Count == 0)
			{
				throw new FormatException(file + ": berths must be a non-empty array.");
			}

			var berths = new List<AuditBerth>();
			var marks = new List<HarborBerthMark>();
			foreach (var token in berthTokens)
			{
				var berth = AuditBerth.Parse(token as JObject ?? throw new FormatException(file + ": every berth must be an object."), file, rowsPerHull);
				berths.Add(berth);
				if (berth.Eligible)
				{
					marks.Add(new HarborBerthMark(berth.StopId, berth.BerthId, berth.Note, null));
				}
			}

			return new HarborLandingAudit(digest, rowsPerHull, berths, marks);
		}

		public sealed class AuditBerth
		{
			private AuditBerth(string stopId, string berthId, bool eligible, IReadOnlyList<AuditHull> hulls, string note)
			{
				StopId = stopId;
				BerthId = berthId;
				Eligible = eligible;
				Hulls = hulls;
				Note = note;
			}

			public string StopId { get; }

			public string BerthId { get; }

			/// <summary>False when the berth has no start pose: its face is excluded from contact (the three crew faces).</summary>
			public bool Eligible { get; }

			public IReadOnlyList<AuditHull> Hulls { get; }

			/// <summary>What the picker shows beside the berth; empty for an ineligible berth.</summary>
			public string Note { get; }

			internal static AuditBerth Parse(JObject token, string file, int rowsPerHull)
			{
				var stopId = Text(token, "stop_id", file);
				var berthId = Text(token, "berth_id", file);
				if (token["eligible"] == null || token["eligible"].Type != JTokenType.Boolean)
				{
					throw new FormatException(file + ": berth " + berthId + " needs a boolean 'eligible'.");
				}

				var eligible = (bool)token["eligible"];
				var hulls = new List<AuditHull>();
				foreach (var hull in token["hulls"] as JArray ?? throw new FormatException(file + ": berth " + berthId + " needs a 'hulls' array."))
				{
					hulls.Add(AuditHull.Parse(hull as JObject ?? throw new FormatException(file + ": every hull must be an object."), file, berthId, rowsPerHull));
				}

				if (hulls.Count == 0)
				{
					throw new FormatException(file + ": berth " + berthId + " has no hulls.");
				}

				return new AuditBerth(stopId, berthId, eligible, hulls, eligible ? NoteFor(hulls) : string.Empty);
			}

			/// <summary>"Landing audit: afloat at every sampled tide", or the afloat counts per hull.</summary>
			public static string NoteFor(IReadOnlyList<AuditHull> hulls)
			{
				var all = true;
				foreach (var hull in hulls)
				{
					all &= hull.Accepted == hull.Rows;
				}

				if (all)
				{
					return "Landing audit: afloat at every sampled tide";
				}

				var note = new StringBuilder("Landing audit: afloat ");
				for (var i = 0; i < hulls.Count; i++)
				{
					if (i > 0)
					{
						note.Append(" · ");
					}

					note.Append(hulls[i].Label).Append(' ').Append(hulls[i].Accepted.ToString(CultureInfo.InvariantCulture)).Append('/').Append(hulls[i].Rows.ToString(CultureInfo.InvariantCulture));
				}

				return note.ToString();
			}

			private static string Text(JObject token, string name, string file)
			{
				var value = (string)token[name];
				if (string.IsNullOrEmpty(value))
				{
					throw new FormatException(file + ": every berth needs a non-empty '" + name + "'.");
				}

				return value;
			}
		}

		public sealed class AuditHull
		{
			private AuditHull(string hull, int rows, int accepted)
			{
				Hull = hull;
				Rows = rows;
				Accepted = accepted;
			}

			/// <summary>The hull as the pipeline names it: <c>t-class</c> or <c>k-class</c>.</summary>
			public string Hull { get; }

			/// <summary>Sampled starts.</summary>
			public int Rows { get; }

			/// <summary>Starts afloat (startable or warning), never aground, excluded or unknown.</summary>
			public int Accepted { get; }

			/// <summary>"T", "K", or the hull name for anything else.</summary>
			public string Label => Hull == "t-class" ? "T" : Hull == "k-class" ? "K" : Hull;

			internal static AuditHull Parse(JObject token, string file, string berthId, int rowsPerHull)
			{
				var hull = (string)token["hull"];
				if (string.IsNullOrEmpty(hull))
				{
					throw new FormatException(file + ": berth " + berthId + " has a hull without a name.");
				}

				var rows = Count(token, "rows", file, berthId);
				var accepted = Count(token, "accepted", file, berthId);
				if (rows != rowsPerHull || accepted > rows)
				{
					throw new FormatException(file + ": berth " + berthId + ", " + hull + ": " + accepted + " accepted of " + rows + " rows, expected " + rowsPerHull + " rows.");
				}

				return new AuditHull(hull, rows, accepted);
			}

			private static int Count(JObject token, string name, string file, string berthId)
			{
				if (token[name] == null || token[name].Type != JTokenType.Integer || (int)token[name] < 0)
				{
					throw new FormatException(file + ": berth " + berthId + " needs a non-negative '" + name + "' count.");
				}

				return (int)token[name];
			}
		}
	}
}
