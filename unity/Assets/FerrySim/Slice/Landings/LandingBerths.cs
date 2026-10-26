using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace FerrySim.Slice
{
	/// <summary>A named selectable face, ordered by the pipeline with the default first.</summary>
	public sealed class LandingBerth
	{
		internal LandingBerth(string id, string name, int order, int[] segmentIds, string crewNote)
		{
			Id = id; Name = name; Order = order; SegmentIds = segmentIds; CrewNote = crewNote;
		}

		public string Id { get; }
		public string Name { get; }
		public int Order { get; }
		public IReadOnlyList<int> SegmentIds { get; }
		public string CrewNote { get; }
	}

	public sealed partial class LandingGeometry
	{
		private readonly Dictionary<string, List<LandingBerth>> _berths = new Dictionary<string, List<LandingBerth>>(StringComparer.Ordinal);

		public IReadOnlyList<LandingBerth> Berths(string stopId) =>
			stopId != null && _berths.TryGetValue(stopId, out var berths) ? berths : Array.Empty<LandingBerth>();

		/// <summary>A complete start-eligible face in the explicitly exported start-to-end order; null or empty means default.</summary>
		public LandingFace BerthFace(string stopId, string berthId = null)
		{
			var berths = Berths(stopId);
			var berth = string.IsNullOrEmpty(berthId) ? (berths.Count > 0 ? berths[0] : null) : berths.FirstOrDefault(b => b.Id == berthId);
			var where = $"stop {stopId} berth {berthId ?? berth?.Id ?? "default"}";
			if (berth == null)
			{
				throw new LandingDataException(ContactsFile, new[] { where + ": unknown berth" });
			}

			var face = new List<LandingSegment>();
			foreach (var id in berth.SegmentIds)
			{
				var segment = Find(id);
				if (segment == null || segment.StopId != stopId)
				{
					throw new LandingDataException(ContactsFile, new[] { where + $": face segment {id} is not kept for this stop with an approved start-face basis" });
				}
				if (!segment.StartFaceEligible)
				{
					throw new LandingDataException(ContactsFile, new[] { where + $": face segment {id} lacks a start-face basis (pipeline water-side proof, crew-reported water side or complete crew-use exception)" });
				}
				if (face.Count > 0 && (segment.Segment.Start - face[face.Count - 1].Segment.End).Magnitude > JointTolerance)
				{
					throw new LandingDataException(ContactsFile, new[] { where + $": segments {face[face.Count - 1].Id} and {id} do not join end to start" });
				}
				face.Add(segment);
			}
			return new LandingFace(face);
		}

		private void ReadBerths(JArray root)
		{
			var problems = new List<string>();
			var byStop = root.OfType<JObject>().GroupBy(r => (string)r["stop_id"]);
			foreach (var stop in byStop)
			{
				var list = new List<LandingBerth>();
				var marked = stop.Where(r => Flag(r, "berth_face", $"stop {stop.Key}", problems)).ToList();
				if (marked.Count == 0)
				{
					// Version 1/2 files without selectable-berth fields retain the primary-face semantics.
					var primary = stop.Where(r => (bool?)r["primary_face"] == true).ToList();
					if (primary.Count > 0)
					{
						list.Add(new LandingBerth((string)primary[0]["berth_id"] ?? "stop:" + stop.Key, "Main berth", 0,
							primary.Select(r => (int)r["id"]).ToArray(), null));
					}
				}
				else foreach (var group in marked.GroupBy(r => (string)r["berth_id"] ?? "stop:" + stop.Key))
				{
					var first = group.First();
					var where = $"stop {stop.Key} berth {group.Key}";
					var name = OptionalText(first, "berth_name", where, problems, true);
					var orderToken = first["berth_order"];
					if (name == null || orderToken?.Type != JTokenType.Integer || !int.TryParse(orderToken.ToString(), out var order) || order < 0 ||
						!(first["berth_face_segment_ids"] is JArray ids) || ids.Count == 0 || ids.Any(id => !TryId(id, out _)))
					{
						problems.Add(where + ": needs a name, nonnegative order and nonempty face segment IDs");
						continue;
					}
					var identifiers = ids.Select(id => (int)id).ToArray();
					if (identifiers.Distinct().Count() != identifiers.Length || !identifiers.OrderBy(i => i).SequenceEqual(group.Select(r => (int)r["id"]).OrderBy(i => i)))
						problems.Add(where + ": face segment IDs must name every marked subdivision exactly once");
					foreach (var item in group)
					{
						if (new[] { "berth_name", "berth_order", "berth_face_segment_ids", "berth_use_crew_note", "berth_use_evidence", "berth_side_evidence", "berth_corridor_structures" }.Any(key => !JToken.DeepEquals(first[key], item[key])))
							problems.Add(where + ": inconsistent berth metadata");
						if (((bool?)item["primary_face"] == true) != (order == 0)) problems.Add(where + ": primary_face must mark exactly the default berth");
					}
					list.Add(new LandingBerth(group.Key, name, order, identifiers, OptionalText(first, "berth_use_crew_note", where, problems, true)));
				}
				if (marked.Count > 0 && stop.Any(r => (bool?)r["primary_face"] == true && !marked.Contains(r)))
					problems.Add($"stop {stop.Key}: default berth has an unmarked primary subdivision");
				list.Sort((a, b) => a.Order.CompareTo(b.Order));
				for (var i = 0; i < list.Count; i++)
				{
					if (list[i].Order != i) problems.Add($"stop {stop.Key}: berth order must be consecutive from zero");
					if (list.Count > 1 && list[i].Id.StartsWith("stop:", StringComparison.Ordinal)) problems.Add($"stop {stop.Key}: a multi-berth landing needs barge berth IDs");
				}
				_berths.Add(stop.Key, list);
			}
			if (problems.Count > 0) throw new LandingDataException(ContactsFile, problems);
		}
	}
}
