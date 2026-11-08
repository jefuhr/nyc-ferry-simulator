using System;
using System.Collections.Generic;
using UnityEngine;

namespace FerrySim.World.Landings
{
	/// <summary>Small build-time mesh batches: vertex colours keep the entire kit on one shared material.</summary>
	internal sealed class LandingMeshBuilder
	{
		internal static readonly Color32 Hull = new Color32(64,75,78,0), Deck = new Color32(155,160,157,0),
			Rubber = new Color32(26,30,31,0), Pale = new Color32(217,218,205,0), Steel = new Color32(125,139,142,0),
			Roof = new Color32(222,220,205,0), Lamp = new Color32(245,228,177,140);
		private readonly List<Vector3> _vertices = new List<Vector3>();
		private readonly List<int> _indices = new List<int>();
		private readonly List<Color32> _colors = new List<Color32>();
		internal int TriangleCount => _indices.Count/3;

		internal void Triangle(Vector3 a, Vector3 b, Vector3 c, Color32 color)
		{
			var start=_vertices.Count; _vertices.Add(a);_vertices.Add(b);_vertices.Add(c);
			_colors.Add(color);_colors.Add(color);_colors.Add(color);
			_indices.Add(start);_indices.Add(start+1);_indices.Add(start+2);
		}
		internal void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color32 color)
		{ Triangle(a,b,c,color);Triangle(a,c,d,color); }
		internal void Box(Vector3 centre, Vector3 size, Quaternion rotation, Color32 color)
		{
			var x=rotation*Vector3.right*size.x*.5f;var y=rotation*Vector3.up*size.y*.5f;var z=rotation*Vector3.forward*size.z*.5f;
			var a=centre-x-y-z;var b=centre+x-y-z;var c=centre+x+y-z;var d=centre-x+y-z;
			var e=centre-x-y+z;var f=centre+x-y+z;var g=centre+x+y+z;var h=centre-x+y+z;
			Quad(a,d,c,b,color);Quad(e,f,g,h,color);Quad(a,e,h,d,color);Quad(b,c,g,f,color);Quad(d,h,g,c,color);Quad(a,b,f,e,color);
		}
		internal void Beam(Vector3 a, Vector3 b, float width, Color32 color, float depth=0)
		{
			var delta=b-a;if(delta.sqrMagnitude<.00001f)return;
			Box((a+b)*.5f,new Vector3(width,depth>0?depth:width,delta.magnitude),Quaternion.LookRotation(delta,Mathf.Abs(Vector3.Dot(delta.normalized,Vector3.up))>.99f?Vector3.forward:Vector3.up),color);
		}
		internal void Cylinder(Vector3 centre, float radius, float bottom, float top, Color32 color)
		{
			for(var i=0;i<10;i++)
			{
				var a=centre+new Vector3(Mathf.Cos(i*Mathf.PI/5),0,Mathf.Sin(i*Mathf.PI/5))*radius;
				var b=centre+new Vector3(Mathf.Cos((i+1)*Mathf.PI/5),0,Mathf.Sin((i+1)*Mathf.PI/5))*radius;
				Quad(a+Vector3.up*bottom,a+Vector3.up*top,b+Vector3.up*top,b+Vector3.up*bottom,color);
				Triangle(centre+Vector3.up*top,b+Vector3.up*top,a+Vector3.up*top,color);
				Triangle(centre+Vector3.up*bottom,a+Vector3.up*bottom,b+Vector3.up*bottom,color);
			}
		}
		internal Mesh Mesh(string name)
		{
			var mesh=new Mesh {name=name};
			if(_vertices.Count>65535) mesh.indexFormat=UnityEngine.Rendering.IndexFormat.UInt32;
			mesh.SetVertices(_vertices);mesh.SetColors(_colors);mesh.SetTriangles(_indices,0);mesh.RecalculateNormals();mesh.RecalculateBounds();
			return mesh;
		}

		internal void HullPrism(List<Vector3> ring, List<int> triangles, LandingStyle style)
		{
			var top=Vector3.up*style.Freeboard;var bottom=Vector3.up*(style.Freeboard-style.Depth);
			for(var i=0;i<triangles.Count;i+=3)
			{
				var a=ring[triangles[i]];var b=ring[triangles[i+1]];var c=ring[triangles[i+2]];
				Triangle(a+top,c+top,b+top,Deck);Triangle(a+bottom,b+bottom,c+bottom,Hull);
			}
			for(var i=0;i<ring.Count;i++)
			{
				var a=ring[i];var b=ring[(i+1)%ring.Count];
				Quad(a+bottom,a+top,b+top,b+bottom,Hull);
				Beam(a+top-Vector3.up*.12f,b+top-Vector3.up*.12f,.18f,Pale,.24f);
			}
		}
		internal static List<Vector3> Clean(Vector3[] outline)
		{
			if(outline==null || outline.Length<3 || outline.Length>256)throw new ArgumentException("Landing needs 3 to 256 outline points.");
			var ring=new List<Vector3>();
			foreach(var value in outline)
			{
				if(!Finite(value))throw new ArgumentException("Landing outline is not finite.");
				var p=new Vector3(value.x,0,value.z);
				if(ring.Count==0 || (ring[ring.Count-1]-p).sqrMagnitude>1e-8f)ring.Add(p);
			}
			if(ring.Count>1 && (ring[0]-ring[ring.Count-1]).sqrMagnitude<1e-8f)ring.RemoveAt(ring.Count-1);
			var area=0.0;for(var i=0;i<ring.Count;i++){var a=ring[i];var b=ring[(i+1)%ring.Count];area+=(double)a.x*b.z-(double)b.x*a.z;}
			if(ring.Count<3 || Math.Abs(area)<.01)throw new ArgumentException("Landing outline has no area.");
			if(area<0)ring.Reverse();
			return ring;
		}
		internal static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
		internal static double Cross(Vector3 a,Vector3 b,Vector3 c) => (double)(b.x-a.x)*(c.z-a.z)-(double)(b.z-a.z)*(c.x-a.x);
		internal static List<int> Triangulate(List<Vector3> ring)
		{
			var remaining=new List<int>();var result=new List<int>();for(var i=0;i<ring.Count;i++)remaining.Add(i);
			while(remaining.Count>2)
			{
				var clipped=false;
				for(var i=0;i<remaining.Count;i++)
				{
					var a=remaining[(i+remaining.Count-1)%remaining.Count];var b=remaining[i];var c=remaining[(i+1)%remaining.Count];
					var turn=Cross(ring[a],ring[b],ring[c]);
					if(Math.Abs(turn)<1e-7){remaining.RemoveAt(i);clipped=true;break;}
					if(turn<0)continue;
					var blocked=false;
					foreach(var j in remaining)if(j!=a && j!=b && j!=c && Cross(ring[a],ring[b],ring[j])>=0 && Cross(ring[b],ring[c],ring[j])>=0 && Cross(ring[c],ring[a],ring[j])>=0){blocked=true;break;}
					if(blocked)continue;
					result.Add(a);result.Add(b);result.Add(c);remaining.RemoveAt(i);clipped=true;break;
				}
				if(!clipped)throw new ArgumentException("Landing outline cannot be triangulated without crossing its boundary.");
			}
			return result;
		}
		internal static bool Inside(Vector3 p,List<Vector3> ring)
		{
			var inside=false;
			for(int i=0,j=ring.Count-1;i<ring.Count;j=i++)
			{
				var a=ring[i];var b=ring[j];
				if((a.z>p.z)!=(b.z>p.z) && p.x<(b.x-a.x)*(p.z-a.z)/(b.z-a.z)+a.x)inside=!inside;
			}
			return inside;
		}
	}
}
