using System;
using System.Collections.Generic;
using System.Globalization;
using FerrySim.Core.World;
using FerrySim.World.Landings;
using UnityEngine;

namespace FerrySim.World.HarborNavigation
{
	public sealed class HarborBargeView : MonoBehaviour
	{
		// ASCE approximate 7 ft freeboard / 11 ft depth. Rendering only; contacts keep their existing geometry.
		public const float EstimatedFreeboard = 2.1336f, EstimatedDraft = 1.2192f;
		public HarborBarge Source { get; internal set; }
		public NavigationAnchor Anchor { get; internal set; }
		public float Freeboard { get; private set; } = EstimatedFreeboard;
		public float Draft { get; private set; } = EstimatedDraft;
		public string Details { get; internal set; }
		public LandingKit Kit { get; private set; }
		private double _initialWater;

		internal void BuildKit(LandingLookLibrary looks, Dictionary<string,List<VisualFaceRecord>> sourceFaces)
		{
			var stopId=Source.StopIds!=null && Source.StopIds.Count>0?Source.StopIds[0].Text:null;
			var style=looks.ForStop(stopId);Freeboard=style.Freeboard;Draft=style.Depth-style.Freeboard;
			_initialWater=Anchor.Height;
			if(Source.Geometry.Polygons.Count!=1 || Source.Geometry.Polygons[0].Rings.Count!=1)
				throw new ArgumentException("A landing kit requires one simple source outline: "+Source.Id);
			var ring=Source.Geometry.Polygons[0].Rings[0];var points=new Vector3[ring.Count];
			for(var i=0;i<points.Length;i++){points[i]=Anchor.Local(ring[i].North,ring[i].East,Anchor.Height);points[i].y=0;}
			var faces=new List<LandingFace>();
			if(sourceFaces.TryGetValue(Source.Id,out var records))
			{
				// Retain file order within primary/secondary groups. No nearest-face remapping.
				for(var primary=1;primary>=0;primary--)foreach(var record in records)
				{
					if(record.primary_face!=(primary==1))continue;
					var a=Anchor.Local(record.start_ne_m[0],record.start_ne_m[1],Anchor.Height);
					var b=Anchor.Local(record.end_ne_m[0],record.end_ne_m[1],Anchor.Height);
					var origin=Anchor.Local(record.start_ne_m[0]+record.water_normal_ne[0],record.start_ne_m[1]+record.water_normal_ne[1],Anchor.Height);
					faces.Add(new LandingFace(a,b,origin-a));
				}
			}
			Kit=LandingKit.Build(transform,points,faces.ToArray(),style);
			Details += "; visual freeboard " + Freeboard.ToString("0.####", CultureInfo.InvariantCulture) + " m, draft " + Draft.ToString("0.####", CultureInfo.InvariantCulture) + " m; visual kit: dimensions from landing-looks.json; shore connection and pile siting estimated; no collision authority";
			if(Kit.FaceOrientationEstimated)Details += "; no assigned berthing face, longest outline edge used as a visual frame only";
		}
		internal void SetWaterHeight(double height)
		{
			Anchor.SetHeight(height);Kit.SetTideOffset((float)(height-_initialWater));
		}

		/// <summary>A build-time copy of display face coordinates, never written back into contacts or the pack.</summary>
		[Serializable] internal sealed class VisualFaceRecord
		{
			public string structure_id,kind;
			public bool berth_face,primary_face;
			public double[] start_ne_m,end_ne_m,water_normal_ne;
		}
		[Serializable] private sealed class FaceEnvelope {public VisualFaceRecord[] faces;}
		internal static Dictionary<string,List<VisualFaceRecord>> ReadVisualFaces(string contactsJson)
		{
			var result=new Dictionary<string,List<VisualFaceRecord>>(StringComparer.Ordinal);
			var envelope=JsonUtility.FromJson<FaceEnvelope>("{\"faces\":"+contactsJson+"}");
			if(envelope?.faces==null)throw new ArgumentException("Missing contact display coordinates.");
			foreach(var record in envelope.faces)
			{
				if(string.IsNullOrEmpty(record.structure_id) || (!record.berth_face && record.kind!="berth-face"))continue;
				if(record.start_ne_m?.Length!=2 || record.end_ne_m?.Length!=2 || record.water_normal_ne?.Length!=2)
					throw new ArgumentException("Invalid visual face coordinates for "+record.structure_id);
				if(!result.TryGetValue(record.structure_id,out var list)){list=new List<VisualFaceRecord>();result.Add(record.structure_id,list);}
				list.Add(record);
			}
			return result;
		}
	}
}
