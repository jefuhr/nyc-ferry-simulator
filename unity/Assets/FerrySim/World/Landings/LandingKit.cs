using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace FerrySim.World.Landings
{
	/// <summary>Three render batches: floating hull/fenders/shelter, fixed guide piles and a hinged gangway.
	/// Tide input is relative to the build-time water height. No collider, physics or core dependency.</summary>
	[ExecuteAlways]
	[DefaultExecutionOrder(120)]
	public sealed class LandingKit : MonoBehaviour
	{
		[SerializeField] private MeshFilter floating, piles, gangway;
		[SerializeField] private Vector3 bargeHinge, shoreHinge;
		[SerializeField] private float gangwayLength;
		[SerializeField] private Transform sliceTideParent;
		[SerializeField] private int fenderPanels, endFenders, canopyFrames, triangleCount;
		private bool _ownsMeshes, _hasSharedGangway;
		private Mesh _floatingOwned, _pilesOwned;
		private (float Width,float Length) _gangwayKey;
		public Mesh FloatingMesh => floating.sharedMesh;
		public Mesh GangwayMesh => gangway.sharedMesh;
		public Transform FixedPiles => piles.transform;
		public Transform Gangway => gangway.transform;
		public Vector3 BargeHinge => bargeHinge;
		public Vector3 ShoreHinge => shoreHinge;
		public int FenderPanels => fenderPanels;
		public int EndFenders => endFenders;
		public int CanopyFrames => canopyFrames;
		public int PileCount => 2;
		public int TriangleCount => triangleCount;
		[SerializeField] private bool faceOrientationEstimated;
		[SerializeField] private float visualFreeboard;
		public bool FaceOrientationEstimated => faceOrientationEstimated;
		public float VisualFreeboard => visualFreeboard;

		public static LandingKit Build(Transform parent, Vector3[] outline, LandingFace[] faces, LandingStyle style)
		{
			if(parent==null || style==null)throw new ArgumentNullException();
			style.Validate();
			var ring=LandingMeshBuilder.Clean(outline);
			var triangles=LandingMeshBuilder.Triangulate(ring);
			var estimated=faces==null || faces.Length==0;
			if(estimated)
			{
				// An unassigned pontoon has no berth authority. Its longest outline edge is only a visual frame.
				var edge=LongestEdge(ring);var a=ring[edge];var b=ring[(edge+1)%ring.Count];
				faces=new[] {new LandingFace(a,b,Vector3.Cross(Vector3.up,b-a))};
			}
			foreach(var face in faces)
				if(!LandingMeshBuilder.Finite(face.Start) || !LandingMeshBuilder.Finite(face.End) || !LandingMeshBuilder.Finite(face.Outward) ||
					(face.End-face.Start).sqrMagnitude<.01 || face.Outward.sqrMagnitude<.9 || Mathf.Abs(Vector3.Dot((face.End-face.Start).normalized,face.Outward))>.05)
					throw new ArgumentException("Landing face needs finite endpoints and a perpendicular outward normal.");
			var floatBatch=new LandingMeshBuilder();var fixedBatch=new LandingMeshBuilder();
			floatBatch.HullPrism(ring,triangles,style);
			var panels=0;
			foreach(var face in faces) panels+=Fenders(floatBatch,face,style);
			var main=faces[0];var inward=-main.Outward;var along=(main.End-main.Start).normalized;
			var rotation=Quaternion.LookRotation(inward,Vector3.up);
			foreach(var p in new[] {main.Start,main.End})
				floatBatch.Box(p+inward*.75f+Vector3.up*(style.Freeboard*.5f),new Vector3(.45f,style.Freeboard+1.2f,1.8f),rotation,LandingMeshBuilder.Rubber);
			var centre=InteriorCentre(ring,triangles);
			var frames=Shelter(floatBatch,ring,centre,style);
			// Ray from an interior point to the rear outline: the shore end is an explicitly unsurveyed span.
			var rear=RayBoundary(centre,inward,ring);
			var hinge=rear+Vector3.up*style.Freeboard;
			var shore=rear+inward*style.GangwayLength+Vector3.up*style.ShoreHeight;
			var min=float.PositiveInfinity;var max=float.NegativeInfinity;
			foreach(var p in ring){var projection=Vector3.Dot(p-rear,along);min=Mathf.Min(min,projection);max=Mathf.Max(max,projection);}
			foreach(var x in new[] {min*.8f,max*.8f})
			{
				var p=rear+along*x+inward*(style.PileDiameter*.65f);
				fixedBatch.Cylinder(p,style.PileDiameter*.5f,-style.Depth,style.PileHeight,LandingMeshBuilder.Rubber);
				fixedBatch.Cylinder(p,style.PileDiameter*.52f,style.PileHeight-.14f,style.PileHeight,LandingMeshBuilder.Steel);
			}
			var material=LandingLookLibrary.Load().Surface;
			if(material==null)throw new InvalidOperationException("Missing landing surface material.");
			var host=new GameObject("Landing kit (visual estimates)");host.transform.SetParent(parent,false);host.layer=parent.gameObject.layer;
			var kit=host.AddComponent<LandingKit>();kit._ownsMeshes=true;kit._gangwayKey=(style.GangwayWidth,style.GangwayLength);
			try
			{
				kit.floating=Part(host.transform,"Hull deck fenders shelter",floatBatch.Mesh("Landing floating batch"),material);
				kit.piles=Part(host.transform,"Fixed guide piles",fixedBatch.Mesh("Landing pile batch"),material);
				kit._floatingOwned=kit.FloatingMesh;kit._pilesOwned=kit.piles.sharedMesh;
				var ramp=AcquireGangway(kit._gangwayKey);kit._hasSharedGangway=true;
				kit.gangway=Part(host.transform,"Shore gangway and rails",ramp,material);
				kit.bargeHinge=hinge;kit.shoreHinge=shore;kit.gangwayLength=style.GangwayLength;
				kit.fenderPanels=panels;kit.endFenders=2;kit.canopyFrames=frames;
				kit.triangleCount=floatBatch.TriangleCount+fixedBatch.TriangleCount+kit.GangwayMesh.triangles.Length/3;
				kit.faceOrientationEstimated=estimated;kit.visualFreeboard=style.Freeboard;
				kit.SetTideOffset(0);return kit;
			}
			catch {Release(host);throw;}
		}

		public void SetTideOffset(float offset)
		{
			if(float.IsNaN(offset) || float.IsInfinity(offset))throw new ArgumentException("Tide offset must be finite.");
			piles.transform.localPosition=Vector3.down*offset;
			var direction=shoreHinge-Vector3.up*offset-bargeHinge;
			gangway.transform.localPosition=bargeHinge;
			gangway.transform.localRotation=Quaternion.LookRotation(direction,Vector3.up);
			gangway.transform.localScale=new Vector3(1,1,direction.magnitude/gangwayLength);
		}

		/// <summary>Slice prefab mode only: FloatOnWater writes this parent's local tide height.</summary>
		public void FollowSliceTide(Transform tideParent) => sliceTideParent=tideParent;
		private void LateUpdate() {if(sliceTideParent!=null && piles!=null && gangway!=null)SetTideOffset(sliceTideParent.localPosition.y);}
		/// <summary>Editor caller saves copies of these filters' meshes, then transfers lifetime to the assets.</summary>
		public void UseBakedMeshes()
		{
			if(!_ownsMeshes)return;
			Release(_floatingOwned);Release(_pilesOwned);if(_hasSharedGangway)ReleaseGangway(_gangwayKey);_ownsMeshes=false;
		}
		private void OnDestroy()
		{
			if(!_ownsMeshes)return;
			Release(_floatingOwned);Release(_pilesOwned);
			if(_hasSharedGangway)ReleaseGangway(_gangwayKey);
		}
		internal static void Release(Object value) {if(value==null)return;if(Application.isPlaying)Object.Destroy(value);else Object.DestroyImmediate(value);}
		private static MeshFilter Part(Transform parent,string name,Mesh mesh,Material material)
		{
			var host=new GameObject(name);host.transform.SetParent(parent,false);host.layer=parent.gameObject.layer;
			var filter=host.AddComponent<MeshFilter>();filter.sharedMesh=mesh;
			var renderer=host.AddComponent<MeshRenderer>();renderer.sharedMaterial=material;
			renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
			return filter;
		}

		private static int Fenders(LandingMeshBuilder batch,LandingFace face,LandingStyle s)
		{
			var delta=face.End-face.Start;var length=delta.magnitude;var along=delta/length;
			var count=Mathf.Clamp(Mathf.FloorToInt(length/s.FenderSpacing),2,96);var added=0;
			for(var i=0;i<count;i++)
			{
				var at=(i+.5f)*length/count;
				if(length>6 && Mathf.Abs(at-length*.5f)<1.15f)continue; // Boarding opening, not a contact omission.
				var p=face.Start+along*at+face.Outward*.19f+Vector3.up*(s.FenderHeight*.5f-.25f);
				batch.Box(p,new Vector3(s.FenderWidth,s.FenderHeight,.34f),Quaternion.LookRotation(face.Outward,Vector3.up),i%2==0?LandingMeshBuilder.Rubber:LandingMeshBuilder.Pale);added++;
			}
			return added;
		}

		private static int LongestEdge(List<Vector3> ring)
		{
			var best=0;for(var i=1;i<ring.Count;i++)if((ring[(i+1)%ring.Count]-ring[i]).sqrMagnitude>(ring[(best+1)%ring.Count]-ring[best]).sqrMagnitude)best=i;
			return best;
		}
		private static Vector3 InteriorCentre(List<Vector3> ring,List<int> triangles)
		{
			var centre=Vector3.zero;foreach(var p in ring)centre+=p;centre/=ring.Count;
			if(LandingMeshBuilder.Inside(centre,ring))return centre;
			var best=0.0;var chosen=centre;
			for(var i=0;i<triangles.Count;i+=3)
			{
				var a=ring[triangles[i]];var b=ring[triangles[i+1]];var c=ring[triangles[i+2]];
				var area=Math.Abs(LandingMeshBuilder.Cross(a,b,c));if(area>best){best=area;chosen=(a+b+c)/3;}
			}
			return chosen;
		}
		private static Vector3 RayBoundary(Vector3 origin,Vector3 direction,List<Vector3> ring)
		{
			var nearest=float.PositiveInfinity;
			for(var i=0;i<ring.Count;i++)
			{
				var a=ring[i]-origin;var edge=ring[(i+1)%ring.Count]-ring[i];
				var denominator=direction.x*edge.z-direction.z*edge.x;if(Mathf.Abs(denominator)<1e-6f)continue;
				var t=(a.x*edge.z-a.z*edge.x)/denominator;var u=(a.x*direction.z-a.z*direction.x)/denominator;
				if(t>=0 && u>=0 && u<=1)nearest=Mathf.Min(nearest,t);
			}
			if(float.IsInfinity(nearest))throw new ArgumentException("No rear outline intersection.");
			return origin+direction*nearest;
		}

		private static int Shelter(LandingMeshBuilder batch,List<Vector3> ring,Vector3 centre,LandingStyle s)
		{
			var i=LongestEdge(ring);var x=(ring[(i+1)%ring.Count]-ring[i]).normalized;var z=Vector3.Cross(x,Vector3.up);
			var length=s.CanopyLength;var width=s.CanopyWidth;
			bool Fits(float l,float w)
			{
				// Sample the full rectangle boundary, not just corners, for concave source outlines.
				for(var sample=0;sample<=16;sample++)
				{
					var t=sample/16f-.5f;
					if(!LandingMeshBuilder.Inside(centre+x*(t*l)+z*w*.5f,ring) || !LandingMeshBuilder.Inside(centre+x*(t*l)-z*w*.5f,ring) ||
						!LandingMeshBuilder.Inside(centre+x*l*.5f+z*(t*w),ring) || !LandingMeshBuilder.Inside(centre-x*l*.5f+z*(t*w),ring))return false;
				}
				return true;
			}
			for(var attempt=0;attempt<60 && !Fits(length,width);attempt++){length*=.92f;width*=.92f;}
			if(!Fits(length,width))throw new ArgumentException("Cannot fit a shelter inside this landing outline.");
			Vector3 P(float along,float across,float height) => centre+x*along+z*across+Vector3.up*height;
			float RoofY(float across) => s.Freeboard+s.CanopyHeight+s.CanopyRise*(1-4*across*across/(width*width));
			for(var segment=0;segment<8;segment++)
			{
				var a=width*(segment/8f-.5f);var b=width*((segment+1)/8f-.5f);
				batch.Quad(P(-length*.5f,a,RoofY(a)),P(-length*.5f,b,RoofY(b)),P(length*.5f,b,RoofY(b)),P(length*.5f,a,RoofY(a)),LandingMeshBuilder.Roof);
			}
			var frames=Mathf.Clamp(Mathf.CeilToInt(length/4)+1,3,8);
			for(var frame=0;frame<frames;frame++)
			{
				var at=length*(frame/(float)(frames-1)-.5f);
				for(var side=-1;side<=1;side+=2)
				{
					var foot=P(at,width*.32f*side,s.Freeboard);
					batch.Beam(foot,P(at,width*.32f*side,RoofY(width*.32f)),.11f,LandingMeshBuilder.Steel);
					batch.Beam(foot+Vector3.up*s.CanopyHeight*.65f,P(at,width*.5f*side,RoofY(width*.5f)),.075f,LandingMeshBuilder.Steel);
					batch.Beam(foot+Vector3.up*s.CanopyHeight*.65f,P(at,0,RoofY(0)),.075f,LandingMeshBuilder.Steel);
				}
				for(var segment=0;segment<8;segment++)
				{
					var a=width*(segment/8f-.5f);var b=width*((segment+1)/8f-.5f);
					batch.Beam(P(at,a,RoofY(a)-.06f),P(at,b,RoofY(b)-.06f),.07f,LandingMeshBuilder.Steel);
				}
			}
			for(var side=-1;side<=1;side+=2)
			{
				batch.Beam(P(-length*.5f,width*.32f*side,RoofY(width*.32f)-.1f),P(length*.5f,width*.32f*side,RoofY(width*.32f)-.1f),.07f,LandingMeshBuilder.Steel);
				batch.Beam(P(-length*.4f,width*.25f*side,RoofY(width*.25f)-.16f),P(length*.4f,width*.25f*side,RoofY(width*.25f)-.16f),.055f,LandingMeshBuilder.Lamp);
			}
			return frames;
		}

		private sealed class SharedGangway {public Mesh Mesh;public int Users;}
		private static readonly Dictionary<(float Width,float Length),SharedGangway> Gangways=new Dictionary<(float,float),SharedGangway>();
		private static Mesh AcquireGangway((float Width,float Length) key)
		{
			if(!Gangways.TryGetValue(key,out var entry))
			{
				var b=new LandingMeshBuilder();var w=key.Width;var l=key.Length;
				b.Box(new Vector3(0,-.08f,l*.5f),new Vector3(w,.16f,l),Quaternion.identity,LandingMeshBuilder.Deck);
				for(var side=-1;side<=1;side+=2)
				{
					for(var rail=1;rail<=3;rail++)b.Beam(new Vector3(side*w*.5f,rail*.36f,0),new Vector3(side*w*.5f,rail*.36f,l),.045f,LandingMeshBuilder.Steel);
					var posts=Mathf.CeilToInt(l/2);
					for(var p=0;p<=posts;p++)b.Beam(new Vector3(side*w*.5f,0,p*l/posts),new Vector3(side*w*.5f,1.1f,p*l/posts),.06f,LandingMeshBuilder.Steel);
				}
				entry=new SharedGangway {Mesh=b.Mesh("Shared shore ramp and rails")};Gangways.Add(key,entry);
			}
			entry.Users++;return entry.Mesh;
		}
		private static void ReleaseGangway((float Width,float Length) key)
		{
			if(!Gangways.TryGetValue(key,out var entry))return;
			if(--entry.Users==0){Release(entry.Mesh);Gangways.Remove(key);}
		}
	}
}
