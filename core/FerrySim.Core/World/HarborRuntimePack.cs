using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.World
{
	/// <summary>Immutable source evidence, including unknown/null fields. No JSON library leaks into the core.</summary>
	public sealed class HarborValue
	{
		private HarborValue(string text, double? number, bool? boolean, IReadOnlyList<HarborValue> items, IReadOnlyDictionary<string, HarborValue> fields)
		{
			Text = text;
			Number = number;
			Boolean = boolean;
			Items = items;
			Fields = fields;
		}

		/// <summary>The immutable value for a null or absent evidence field.</summary>
		public static HarborValue Null { get; } = new HarborValue(null, null, null, null, null);

		/// <summary>Text evidence, or null for another value kind.</summary>
		public string Text { get; }

		/// <summary>Finite numeric evidence, or null for another value kind.</summary>
		public double? Number { get; }

		/// <summary>Boolean evidence, or null for another value kind.</summary>
		public bool? Boolean { get; }

		/// <summary>Immutable array evidence, or null for another value kind.</summary>
		public IReadOnlyList<HarborValue> Items { get; }

		/// <summary>Immutable object evidence, or null for another value kind.</summary>
		public IReadOnlyDictionary<string, HarborValue> Fields { get; }

		/// <summary>Whether the evidence is null or absent.</summary>
		public bool IsNull => Text == null && Number == null && Boolean == null && Items == null && Fields == null;

		/// <summary>Reads an optional field, returning Null when absent.</summary>
		public HarborValue this[string name] => Fields != null && Fields.TryGetValue(name, out var value) ? value : Null;

		/// <summary>Creates a text evidence value.</summary>
		public static HarborValue FromText(string text) => new HarborValue(text ?? throw new ArgumentNullException(nameof(text)), null, null, null, null);

		/// <summary>Creates a finite numeric evidence value.</summary>
		public static HarborValue FromNumber(double value)
		{
			if (double.IsNaN(value) || double.IsInfinity(value))
			{
				throw new ArgumentException("Harbor numbers must be finite.");
			}

			return new HarborValue(null, value, null, null, null);
		}

		/// <summary>Creates a boolean evidence value.</summary>
		public static HarborValue FromBoolean(bool value) => new HarborValue(null, null, value, null, null);

		/// <summary>Copies array evidence into read-only storage.</summary>
		public static HarborValue FromItems(IReadOnlyList<HarborValue> values) => new HarborValue(null, null, null, HarborLists.Copy(values), null);

		/// <summary>Copies object evidence into read-only storage.</summary>
		public static HarborValue FromFields(IDictionary<string, HarborValue> fields) => new HarborValue(null, null, null, null, new ReadOnlyDictionary<string, HarborValue>(new Dictionary<string, HarborValue>(fields, StringComparer.Ordinal)));
	}

	internal static class HarborLists
	{
		/// <summary>Copies a collection into read-only storage.</summary>
		public static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source)
		{
			var result = new T[source.Count];
			for (var i = 0; i < result.Length; i++)
			{
				result[i] = source[i];
			}

			return Array.AsReadOnly(result);
		}
	}

	/// <summary>Optional NAVD88 vertical bounds in metres. Null means unknown.</summary>
	public readonly struct HarborVerticalBounds
	{
		/// <summary>Creates an immutable value from the supplied data.</summary>
		public HarborVerticalBounds(double? minimum, double? maximum)
		{
			Minimum = minimum;
			Maximum = maximum;
		}

		/// <summary>Lower NAVD88 bound in metres; null when unknown.</summary>
		public double? Minimum { get; }

		/// <summary>Upper NAVD88 bound in metres; null when unknown.</summary>
		public double? Maximum { get; }
	}

	/// <summary>A polygon with exterior first, then holes, all in world north/east metres.</summary>
	public sealed class HarborPolygon
	{
		/// <summary>Creates an immutable value from the supplied data.</summary>
		public HarborPolygon(IReadOnlyList<IReadOnlyList<HorizontalVector>> rings)
		{
			var copy = new IReadOnlyList<HorizontalVector>[rings.Count];
			for (var i = 0; i < copy.Length; i++)
			{
				copy[i] = HarborLists.Copy(rings[i]);
			}

			Rings = Array.AsReadOnly(copy);
		}

		/// <summary>Closed world north/east rings, exterior first and holes afterwards.</summary>
		public IReadOnlyList<IReadOnlyList<HorizontalVector>> Rings { get; }

		/// <summary>Tests whether a point is inside the exterior and outside every hole.</summary>
		public bool Contains(double north, double east)
		{
			if (Rings.Count == 0 || !InRing(Rings[0], north, east))
			{
				return false;
			}

			for (var i = 1; i < Rings.Count; i++)
			{
				if (InRing(Rings[i], north, east))
				{
					return false;
				}
			}

			return true;
		}

		private static bool InRing(IReadOnlyList<HorizontalVector> ring, double north, double east)
		{
			var inside = false;
			for (var i = 1; i < ring.Count; i++)
			{
				var a = ring[i - 1];
				var b = ring[i];
				if ((a.East > east) != (b.East > east) && north < (b.North - a.North) * (east - a.East) / (b.East - a.East) + a.North)
				{
					inside = !inside;
				}
			}

			return inside;
		}
	}

	/// <summary>Semantic geometry, never collision geometry. Collections preserve polygon holes and line/point forms.</summary>
	public sealed class HarborGeometry
	{
		/// <summary>Creates an immutable value from the supplied data.</summary>
		public HarborGeometry(string type, IReadOnlyList<HarborPolygon> polygons, IReadOnlyList<IReadOnlyList<HorizontalVector>> lines, IReadOnlyList<HorizontalVector> points)
		{
			Type = type;
			Polygons = HarborLists.Copy(polygons);
			Points = HarborLists.Copy(points);
			var copy = new IReadOnlyList<HorizontalVector>[lines.Count];
			for (var i = 0; i < copy.Length; i++)
			{
				copy[i] = HarborLists.Copy(lines[i]);
			}

			Lines = Array.AsReadOnly(copy);
			double minN = double.PositiveInfinity, minE = double.PositiveInfinity, maxN = double.NegativeInfinity, maxE = double.NegativeInfinity;
			void Include(HorizontalVector p)
			{
				minN = Math.Min(minN, p.North);
				minE = Math.Min(minE, p.East);
				maxN = Math.Max(maxN, p.North);
				maxE = Math.Max(maxE, p.East);
			}

			foreach (var polygon in Polygons)
			{
				foreach (var ring in polygon.Rings)
				{
					foreach (var p in ring)
					{
						Include(p);
					}
				}
			}

			foreach (var line in Lines)
			{
				foreach (var p in line)
				{
					Include(p);
				}
			}

			foreach (var p in Points)
			{
				Include(p);
			}

			MinimumNorth = minN;
			MinimumEast = minE;
			MaximumNorth = maxN;
			MaximumEast = maxE;
		}

		/// <summary>The exported geometry type.</summary>
		public string Type { get; }

		/// <summary>Polygon components, including their holes.</summary>
		public IReadOnlyList<HarborPolygon> Polygons { get; }

		/// <summary>Line components in world north/east metres.</summary>
		public IReadOnlyList<IReadOnlyList<HorizontalVector>> Lines { get; }

		/// <summary>Point components in world north/east metres.</summary>
		public IReadOnlyList<HorizontalVector> Points { get; }

		/// <summary>Southern bounding coordinate in world metres.</summary>
		public double MinimumNorth { get; }

		/// <summary>Western bounding coordinate in world metres.</summary>
		public double MinimumEast { get; }

		/// <summary>Northern bounding coordinate in world metres.</summary>
		public double MaximumNorth { get; }

		/// <summary>Eastern bounding coordinate in world metres.</summary>
		public double MaximumEast { get; }
	}

	/// <summary>A chart aid at a world north/east position, with all source light and chart properties.</summary>
	public sealed class HarborAid
	{
		/// <summary>Creates an immutable value from the supplied data.</summary>
		public HarborAid(string id, string sourceId, HorizontalVector position, HarborValue record)
		{
			Id = id;
			SourceId = sourceId;
			Position = position;
			Record = record;
		}

		/// <summary>Stable exported identity.</summary>
		public string Id { get; }

		/// <summary>Original chart feature identity.</summary>
		public string SourceId { get; }

		/// <summary>Position in world north/east metres, retained as doubles.</summary>
		public HorizontalVector Position { get; }

		/// <summary>All immutable source fields, including optional metadata and provenance.</summary>
		public HarborValue Record { get; }

		/// <summary>Original chart properties, including light characteristics.</summary>
		public HarborValue Properties => Record["properties"];
	}

	/// <summary>One charted span of a physical bridge; clearances and unknown vertical bounds retain their source datums.</summary>
	public sealed class HarborSpan
	{
		/// <summary>Creates an immutable value from the supplied data.</summary>
		public HarborSpan(string id, string sourceId, HarborGeometry geometry, HarborVerticalBounds bounds, HarborValue record)
		{
			Id = id;
			SourceId = sourceId;
			Geometry = geometry;
			VerticalBounds = bounds;
			Record = record;
		}

		/// <summary>Stable exported identity.</summary>
		public string Id { get; }

		/// <summary>Original chart feature identity.</summary>
		public string SourceId { get; }

		/// <summary>Resident semantic geometry in world north/east metres.</summary>
		public HarborGeometry Geometry { get; }

		/// <summary>NAVD88 vertical bounds, preserving unknown values.</summary>
		public HarborVerticalBounds VerticalBounds { get; }

		/// <summary>All immutable source fields, including optional metadata and provenance.</summary>
		public HarborValue Record { get; }

		/// <summary>Per-source clearance records; MHW clearances never imply a NAVD88 underside.</summary>
		public IReadOnlyList<HarborValue> Clearances => Record["clearances"].Items;

		/// <summary>Whether the exporter asserts a complete pier inventory.</summary>
		public bool PierInventoryComplete => Record["pier_inventory_complete"].Boolean == true;
	}

	/// <summary>A charted pier geometrically associated with bridge spans; membership is not surveyed attachment.</summary>
	public sealed class HarborPier
	{
		/// <summary>Creates an immutable value from the supplied data.</summary>
		public HarborPier(string id, string sourceId, HarborGeometry geometry, HarborVerticalBounds bounds, HarborValue record)
		{
			Id = id;
			SourceId = sourceId;
			Geometry = geometry;
			VerticalBounds = bounds;
			Record = record;
		}

		/// <summary>Stable exported identity.</summary>
		public string Id { get; }

		/// <summary>Original chart feature identity.</summary>
		public string SourceId { get; }

		/// <summary>Resident semantic geometry in world north/east metres.</summary>
		public HarborGeometry Geometry { get; }

		/// <summary>NAVD88 vertical bounds, preserving unknown values.</summary>
		public HarborVerticalBounds VerticalBounds { get; }

		/// <summary>All immutable source fields, including optional metadata and provenance.</summary>
		public HarborValue Record { get; }

		/// <summary>Source evidence for the geometric span associations.</summary>
		public IReadOnlyList<HarborValue> MembershipEvidence => Record["membership_evidence"].Items;
	}

	/// <summary>A physical bridge group with its source spans, intersecting piers and audit evidence.</summary>
	public sealed class HarborBridge
	{
		/// <summary>Creates an immutable value from the supplied data.</summary>
		public HarborBridge(string id, IReadOnlyList<HarborSpan> spans, IReadOnlyList<HarborPier> piers, HarborValue record)
		{
			Id = id;
			Spans = HarborLists.Copy(spans);
			Piers = HarborLists.Copy(piers);
			Record = record;
		}

		/// <summary>Stable exported identity.</summary>
		public string Id { get; }

		/// <summary>The physical group's immutable source spans.</summary>
		public IReadOnlyList<HarborSpan> Spans { get; }

		/// <summary>Charted piers intersecting this group's spans.</summary>
		public IReadOnlyList<HarborPier> Piers { get; }

		/// <summary>All immutable source fields, including optional metadata and provenance.</summary>
		public HarborValue Record { get; }

		/// <summary>Source names for the physical group.</summary>
		public IReadOnlyList<HarborValue> Names => Record["names"].Items;

		/// <summary>The bridge grouping audit evidence.</summary>
		public HarborValue AuditEvidence => Record["audit_evidence"];
	}

	/// <summary>A landing berth and its exported metadata. The contact gate decides start eligibility.</summary>
	public sealed class HarborBerth
	{
		/// <summary>Creates an immutable value from the supplied data.</summary>
		public HarborBerth(string id, string structureId, HarborValue record)
		{
			Id = id;
			StructureId = structureId;
			Record = record;
		}

		/// <summary>Stable exported identity.</summary>
		public string Id { get; }

		/// <summary>The barge identity this berth references.</summary>
		public string StructureId { get; }

		/// <summary>All immutable source fields, including optional metadata and provenance.</summary>
		public HarborValue Record { get; }

		/// <summary>Exported display name.</summary>
		public string Name => Record["berth_name"].Text ?? string.Empty;

		/// <summary>Whether this berth explicitly carries the primary-face flag.</summary>
		public bool PrimaryFace => Record["primary_face"].Boolean == true;
	}

	/// <summary>A landing identity and its source-ordered berths.</summary>
	public sealed class HarborStop
	{
		/// <summary>Creates an immutable value from the supplied data.</summary>
		public HarborStop(string id, string name, HorizontalVector position, IReadOnlyList<HarborBerth> berths, HarborValue record)
		{
			Id = id;
			Name = name;
			Position = position;
			Berths = HarborLists.Copy(berths);
			Record = record;
		}

		/// <summary>Stable exported identity.</summary>
		public string Id { get; }

		/// <summary>Exported display name.</summary>
		public string Name { get; }

		/// <summary>Position in world north/east metres, retained as doubles.</summary>
		public HorizontalVector Position { get; }

		/// <summary>Berths in their exported order.</summary>
		public IReadOnlyList<HarborBerth> Berths { get; }

		/// <summary>All immutable source fields, including optional metadata and provenance.</summary>
		public HarborValue Record { get; }
	}

	/// <summary>A closed floating barge outline, shared by every berth that references its structure identity.</summary>
	public sealed class HarborBarge
	{
		/// <summary>Creates an immutable value from the supplied data.</summary>
		public HarborBarge(string id, HarborGeometry geometry, HarborVerticalBounds bounds, HarborValue record)
		{
			Id = id;
			Geometry = geometry;
			VerticalBounds = bounds;
			Record = record;
		}

		/// <summary>Stable exported identity.</summary>
		public string Id { get; }

		/// <summary>Resident semantic geometry in world north/east metres.</summary>
		public HarborGeometry Geometry { get; }

		/// <summary>NAVD88 vertical bounds, preserving unknown values.</summary>
		public HarborVerticalBounds VerticalBounds { get; }

		/// <summary>All immutable source fields, including optional metadata and provenance.</summary>
		public HarborValue Record { get; }

		/// <summary>Whether the outline is identified as floating.</summary>
		public bool Floating => Record["floating"].Boolean == true;

		/// <summary>Landing identities associated with this barge.</summary>
		public IReadOnlyList<HarborValue> StopIds => Record["stop_ids"].Items;
	}

	/// <summary>A semantic chart/camera/radar footprint with no collision authority.</summary>
	public sealed class HarborFootprint
	{
		/// <summary>Creates an immutable value from the supplied data.</summary>
		public HarborFootprint(string id, string semantic, HarborGeometry geometry, HarborVerticalBounds bounds, HarborValue record)
		{
			Id = id;
			Semantic = semantic;
			Geometry = geometry;
			VerticalBounds = bounds;
			Record = record;
		}

		/// <summary>Stable exported identity.</summary>
		public string Id { get; }

		/// <summary>The footprint category: shoreline, structure, bridge or barge.</summary>
		public string Semantic { get; }

		/// <summary>Resident semantic geometry in world north/east metres.</summary>
		public HarborGeometry Geometry { get; }

		/// <summary>NAVD88 vertical bounds, preserving unknown values.</summary>
		public HarborVerticalBounds VerticalBounds { get; }

		/// <summary>All immutable source fields, including optional metadata and provenance.</summary>
		public HarborValue Record { get; }

		/// <summary>Always false: semantic footprints never supply contacts or colliders.</summary>
		public bool CollisionAuthority => false;
	}

	/// <summary>Resident verified-export data, independent of renderer residency. Load through HarborRuntimePackJson.</summary>
	public sealed class HarborRuntimePack
	{
		/// <summary>Creates an immutable value from the supplied data.</summary>
		public HarborRuntimePack(string region, string digest, string depthDigest, IReadOnlyList<HarborAid> aids, IReadOnlyList<HarborBridge> bridges, IReadOnlyList<HarborStop> stops, IReadOnlyList<HarborBarge> barges, HarborGeoidLattice geoid, HarborGeometry coverage, IReadOnlyList<HarborFootprint> footprints, HarborValue manifest, string contactsJson)
		{
			Region = region;
			Digest = digest;
			EnvironmentDepthDigest = depthDigest;
			Aids = HarborLists.Copy(aids);
			Bridges = HarborLists.Copy(bridges);
			Stops = HarborLists.Copy(stops);
			Barges = HarborLists.Copy(barges);
			Geoid = geoid;
			Coverage = coverage;
			Footprints = HarborLists.Copy(footprints);
			Manifest = manifest;
			ContactsJson = contactsJson;
			Queries = new HarborQueryIndex(Footprints);
		}

		/// <summary>The exported region identity.</summary>
		public string Region { get; }

		/// <summary>The raw-byte runtime pack digest.</summary>
		public string Digest { get; }

		/// <summary>The depth digest linked by the runtime manifest.</summary>
		public string EnvironmentDepthDigest { get; }

		/// <summary>Resident chart aids, including boundary candidates outside the active datum mask.</summary>
		public IReadOnlyList<HarborAid> Aids { get; }

		/// <summary>Resident physical bridge groups.</summary>
		public IReadOnlyList<HarborBridge> Bridges { get; }

		/// <summary>All landing identities in source order, independent of start eligibility.</summary>
		public IReadOnlyList<HarborStop> Stops { get; }

		/// <summary>Unique barge outlines by physical structure identity.</summary>
		public IReadOnlyList<HarborBarge> Barges { get; }

		/// <summary>The shared-node canonical NAVD88-zero ellipsoidal height lattice.</summary>
		public HarborGeoidLattice Geoid { get; }

		/// <summary>The exported active harbor coverage geometry.</summary>
		public HarborGeometry Coverage { get; }

		/// <summary>Resident semantic query footprints; none has collision authority.</summary>
		public IReadOnlyList<HarborFootprint> Footprints { get; }

		/// <summary>The complete immutable runtime manifest.</summary>
		public HarborValue Manifest { get; }

		/// <summary>Verified original contact JSON text. Parse with the existing contact authority, never footprint geometry.</summary>
		public string ContactsJson { get; }

		/// <summary>The deterministic resident footprint broad-phase index.</summary>
		public HarborQueryIndex Queries { get; }
	}
}
