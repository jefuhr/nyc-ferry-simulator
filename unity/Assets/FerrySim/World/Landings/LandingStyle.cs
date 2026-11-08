using System;
using UnityEngine;

namespace FerrySim.World.Landings
{
	/// <summary>Rendering dimensions only. No simulation or contact types enter this assembly.</summary>
	[Serializable]
	public sealed class LandingStyle
	{
		public float Freeboard = 2.1336f, Depth = 3.3528f;
		public float FenderSpacing = .9f, FenderHeight = 3.5f, FenderWidth = .45f;
		public float CanopyLength = 22, CanopyWidth = 7.5f, CanopyHeight = 3.4f, CanopyRise = .65f;
		public float GangwayLength = 14, GangwayWidth = 1.8f, ShoreHeight = 3.2f;
		public float PileHeight = 8, PileDiameter = .85f;

		internal void Validate()
		{
			var values = new[] { Freeboard, Depth, FenderSpacing, FenderHeight, FenderWidth, CanopyLength,
				CanopyWidth, CanopyHeight, CanopyRise, GangwayLength, GangwayWidth, ShoreHeight, PileHeight, PileDiameter };
			foreach (var value in values) if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0 || value > 100)
				throw new ArgumentException("Landing dimensions must be finite, positive and at most 100 metres.");
			if (Depth <= Freeboard || FenderSpacing < .2f || FenderWidth > FenderSpacing || GangwayLength < 2)
				throw new ArgumentException("Invalid landing depth, panel spacing or gangway span.");
		}
	}

	/// <summary>A supplied local visual face. X/Z horizontal metres; never a collision segment.</summary>
	public readonly struct LandingFace
	{
		public readonly Vector3 Start, End, Outward;
		public LandingFace(Vector3 start, Vector3 end, Vector3 outward)
		{
			start.y = end.y = outward.y = 0;
			Start = start; End = end; Outward = outward.normalized;
		}
	}
}
