using System;
using System.Collections.Generic;
using FerrySim.Core.Traffic;
using UnityEngine;

namespace FerrySim.Traffic
{
	/// <summary>
	/// Greybox hulls for traffic, one shape per class, sized from the vessel's length, beam and draft: a hull with a pointed bow
	/// from the keel to the deck, and a superstructure. Freeboard, superstructure size and place are greybox estimates by class
	/// (README.md, The hulls), not the vessels' real shapes. Built in the hull's frame: x starboard, y up from the waterline,
	/// z forward from the reported position, which is the hull's middle (MarineCadastre gives no antenna offsets).
	/// </summary>
	public static class TrafficHullMesh
	{
		// Per class, in TrafficClass order: freeboard as a fraction of length (clamped below), then the superstructure's length
		// and beam as fractions of the hull's, its height in metres (at least), and its centre, as a fraction of length forward.
		private static readonly float[] Freeboard = { 0.08f, 0.07f, 0.09f, 0.06f, 0.05f, 0.1f, 0.08f, 0.09f, 0.09f, 0.1f, 0.1f, 0.07f };
		private static readonly float[] HouseLength = { 0.4f, 0.75f, 0.35f, 0.12f, 0.12f, 0.45f, 0.3f, 0.3f, 0.45f, 0.45f, 0.45f, 0.4f };
		private static readonly float[] HouseBeam = { 0.7f, 0.9f, 0.6f, 0.9f, 0.9f, 0.7f, 0.6f, 0.7f, 0.7f, 0.7f, 0.7f, 0.7f };
		private static readonly float[] HouseHeight = { 2.5f, 3.0f, 4.0f, 12.0f, 12.0f, 1.6f, 1.2f, 2.5f, 2.0f, 2.2f, 2.0f, 3.0f };
		private static readonly float[] HouseAt = { 0.0f, -0.05f, 0.12f, -0.4f, -0.4f, -0.05f, -0.1f, 0.15f, 0.0f, 0.05f, 0.0f, 0.0f };

		/// <summary>The class's greybox colour.</summary>
		public static Color ClassColor(TrafficClass vesselClass)
		{
			switch (vesselClass)
			{
				case TrafficClass.FerryPassenger: return new Color(0.86f, 0.86f, 0.82f);
				case TrafficClass.TugTow: return new Color(0.55f, 0.16f, 0.12f);
				case TrafficClass.Cargo: return new Color(0.26f, 0.31f, 0.40f);
				case TrafficClass.Tanker: return new Color(0.40f, 0.22f, 0.16f);
				case TrafficClass.Pleasure: return new Color(0.93f, 0.93f, 0.93f);
				case TrafficClass.Sailing: return new Color(0.80f, 0.82f, 0.85f);
				case TrafficClass.Fishing: return new Color(0.18f, 0.45f, 0.45f);
				case TrafficClass.LawEnforcement: return new Color(0.12f, 0.20f, 0.45f);
				case TrafficClass.Pilot: return new Color(0.90f, 0.50f, 0.10f);
				case TrafficClass.SearchRescue: return new Color(0.85f, 0.30f, 0.15f);
				case TrafficClass.Military: return new Color(0.45f, 0.47f, 0.48f);
				default: return new Color(0.55f, 0.55f, 0.55f);
			}
		}

		/// <summary>The hull's mesh and the height of its top above the waterline, m.</summary>
		public static Mesh Build(TrafficClass vesselClass, TrafficHullSize size, out double height)
		{
			var c = (int)vesselClass;
			if (c < 0 || c >= Freeboard.Length)
			{
				c = 0;
			}

			var length = (float)size.Length;
			var beam = (float)size.Beam;
			var draft = (float)Math.Min(size.Draft, 0.6 * size.Length);
			var freeboard = Mathf.Clamp(Freeboard[c] * length, 0.5f, 12.0f);
			var vertices = new List<Vector3>();
			var triangles = new List<int>();
			// The hull: a box from the stern to the bow shoulder, three quarters aft, then a wedge to the stem.
			var shoulder = 0.5f * length - Mathf.Min(0.2f * length, 2.5f * beam);
			Prism(vertices, triangles, -0.5f * length, shoulder, 0.5f * length, 0.5f * beam, -draft, freeboard);
			var houseLength = HouseLength[c] * length;
			var houseBeam = HouseBeam[c] * beam;
			var houseHeight = Mathf.Max(HouseHeight[c], 0.0f);
			if (vesselClass == TrafficClass.Cargo || vesselClass == TrafficClass.Tanker)
			{
				// A ship's house rises with its size; a container deck stacks forward of it.
				houseHeight = Mathf.Max(houseHeight, 0.08f * length);
			}

			var houseCentre = HouseAt[c] * length;
			Box(vertices, triangles, houseCentre - 0.5f * houseLength, houseCentre + 0.5f * houseLength, 0.5f * houseBeam, freeboard, freeboard + houseHeight);
			var top = freeboard + houseHeight;
			if (vesselClass == TrafficClass.Cargo)
			{
				var stack = Mathf.Max(2.6f, 0.03f * length);
				Box(vertices, triangles, -0.3f * length, shoulder - 0.05f * length, 0.45f * beam, freeboard, freeboard + stack);
			}
			else if (vesselClass == TrafficClass.Sailing)
			{
				// A mast about the boat's length tall.
				var mast = Mathf.Max(2.0f, 1.1f * length);
				Box(vertices, triangles, 0.05f * length - 0.1f, 0.05f * length + 0.1f, 0.1f, freeboard, freeboard + mast);
				top = freeboard + mast;
			}

			height = top;
			var mesh = new Mesh { name = "Traffic hull (" + TrafficClasses.Name(vesselClass) + ")" };
			mesh.SetVertices(vertices);
			mesh.SetTriangles(triangles, 0);
			mesh.RecalculateNormals();
			mesh.RecalculateBounds();
			return mesh;
		}

		/// <summary>A box from <paramref name="aft"/> to <paramref name="fore"/> (z), half-width <paramref name="half"/> (x), bottom to top (y).</summary>
		private static void Box(List<Vector3> v, List<int> t, float aft, float fore, float half, float bottom, float top)
		{
			Prism(v, t, aft, fore, fore, half, bottom, top);
		}

		/// <summary>
		/// A box from <paramref name="aft"/> to <paramref name="shoulder"/>, closed by a wedge to a stem at <paramref name="stem"/>
		/// (no wedge when the two are equal). Flat-shaded: every face has its own vertices.
		/// </summary>
		private static void Prism(List<Vector3> v, List<int> t, float aft, float shoulder, float stem, float half, float bottom, float top)
		{
			var a = new Vector3(-half, bottom, aft);
			var b = new Vector3(half, bottom, aft);
			var c = new Vector3(half, bottom, shoulder);
			var d = new Vector3(-half, bottom, shoulder);
			var e = new Vector3(-half, top, aft);
			var f = new Vector3(half, top, aft);
			var g = new Vector3(half, top, shoulder);
			var h = new Vector3(-half, top, shoulder);
			Quad(v, t, a, b, f, e); // transom
			Quad(v, t, b, c, g, f); // starboard side
			Quad(v, t, c, d, h, g); // forward face (hidden behind the wedge when there is one)
			Quad(v, t, d, a, e, h); // port side
			Quad(v, t, e, f, g, h); // deck
			Quad(v, t, a, d, c, b); // bottom
			if (stem > shoulder)
			{
				var keel = new Vector3(0f, bottom, stem);
				var head = new Vector3(0f, top, stem);
				Quad(v, t, c, keel, head, g); // starboard bow
				Quad(v, t, keel, d, h, head); // port bow
				Triangle(v, t, g, head, h); // foredeck
				Triangle(v, t, c, d, keel); // forefoot
			}
		}

		private static void Quad(List<Vector3> v, List<int> t, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
		{
			// Wound clockwise seen from outside, Unity's front face.
			var i = v.Count;
			v.Add(p0);
			v.Add(p1);
			v.Add(p2);
			v.Add(p3);
			t.Add(i);
			t.Add(i + 2);
			t.Add(i + 1);
			t.Add(i);
			t.Add(i + 3);
			t.Add(i + 2);
		}

		private static void Triangle(List<Vector3> v, List<int> t, Vector3 p0, Vector3 p1, Vector3 p2)
		{
			var i = v.Count;
			v.Add(p0);
			v.Add(p1);
			v.Add(p2);
			t.Add(i);
			t.Add(i + 2);
			t.Add(i + 1);
		}
	}
}
