using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.World.Landings.Tests
{
	/// <summary>Failure modes: missing parts, nondeterministic geometry, duplicated resources, moving piles,
	/// disconnected gangway, mutated source outline, silent invalid geometry and hidden collision geometry.</summary>
	public class LandingKitTests
	{
		private GameObject _root;
		private static Vector3[] Outline => new[] { new Vector3(-14,0,-5), new Vector3(14,0,-5), new Vector3(14,0,5), new Vector3(-14,0,5), new Vector3(-14,0,-5) };
		private static LandingFace[] Faces => new[] { new LandingFace(new Vector3(-14,0,-5), new Vector3(14,0,-5), Vector3.back) };
		[SetUp] public void Setup() => _root = new GameObject("landing test");
		[TearDown] public void Cleanup() => Object.DestroyImmediate(_root);
		private LandingKit Build() => LandingKit.Build(_root.transform, Outline, Faces, new LandingStyle());

		[Test] public void CompleteKitIsBoundedDeterministicAndRenderOnly()
		{
			var a = Build(); var b = Build();
			Assert.That(a.FenderPanels, Is.InRange(20, 40));
			Assert.That(a.EndFenders, Is.EqualTo(2));
			Assert.That(a.CanopyFrames, Is.InRange(3, 8));
			Assert.That(a.PileCount, Is.EqualTo(2));
			Assert.That(a.FloatingMesh, Is.Not.Null);
			Assert.That(a.FloatingMesh.vertices, Is.EqualTo(b.FloatingMesh.vertices));
			Assert.That(a.FloatingMesh.triangles, Is.EqualTo(b.FloatingMesh.triangles));
			Assert.That(a.FloatingMesh.colors32, Is.EqualTo(b.FloatingMesh.colors32));
			Assert.That(a.TriangleCount, Is.InRange(400, 4000));
			Assert.That(a.GetComponentsInChildren<Renderer>().Length, Is.EqualTo(3));
			Assert.That(a.GetComponentsInChildren<Collider>(), Is.Empty);
			Assert.That(a.GetComponentsInChildren<Rigidbody>(), Is.Empty);
			Assert.That(a.GangwayMesh, Is.SameAs(b.GangwayMesh));
			Assert.That(a.GetComponentsInChildren<Renderer>().Select(r => r.sharedMaterial).Distinct().Count(), Is.EqualTo(1));
			Assert.That(a.FloatingMesh.colors32.Distinct().Count(), Is.GreaterThan(4), "neutral hull, deck, black/white fenders and shelter");
		}

		[Test] public void HullKeepsTheSuppliedOutlineAndPublishedVerticalDimensions()
		{
			var source = Outline; var original = source.ToArray();
			var kit = LandingKit.Build(_root.transform, source, Faces, new LandingStyle());
			Assert.That(source, Is.EqualTo(original));
			Assert.That(kit.FloatingMesh, Is.Not.Null);
			var v = kit.FloatingMesh.vertices;
			foreach (var point in source.Take(4))
			{
				Assert.That(v.Any(p => Vector3.Distance(p, point + Vector3.up * 2.1336f) < .0001f), Is.True, "original hull deck corner");
				Assert.That(v.Any(p => Vector3.Distance(p, point - Vector3.up * 1.2192f) < .0001f), Is.True, "published depth minus freeboard");
			}
		}

		[Test] public void TideKeepsPilesAndShoreEndFixedWhileTheBargeEndRises()
		{
			var kit = Build();
			Assert.That(kit.FixedPiles, Is.Not.Null); Assert.That(kit.Gangway, Is.Not.Null);
			var pile = kit.FixedPiles.position;
			var shore = kit.transform.TransformPoint(kit.ShoreHinge);
			var barge = kit.transform.TransformPoint(kit.BargeHinge);
			foreach (var tide in new[] { 1.4f, -.8f, 0f })
			{
				_root.transform.position = Vector3.up * tide; kit.SetTideOffset(tide);
				Assert.That(Vector3.Distance(pile, kit.FixedPiles.position), Is.LessThan(.0001f));
				Assert.That(Vector3.Distance(shore, kit.Gangway.TransformPoint(Vector3.forward * new LandingStyle().GangwayLength)), Is.LessThan(.001f));
				Assert.That(Vector3.Distance(barge + Vector3.up * tide, kit.Gangway.position), Is.LessThan(.001f));
			}
		}

		[Test] public void PackagedSourceValuesAndClipOverridesResolveWithoutEditingDefaults()
		{
			var library=LandingLookLibrary.Load();
			Assert.That(library.Surface.shader.name, Is.EqualTo("FerrySim/LandingSurface"));
			var generic=library.ForStop("unmeasured");var corlears=library.ForStop("115");
			Assert.That(generic.Freeboard,Is.EqualTo(2.1336f));Assert.That(generic.Depth,Is.EqualTo(3.3528f));
			Assert.That(corlears.CanopyWidth,Is.EqualTo(8));Assert.That(corlears.CanopyHeight,Is.EqualTo(3.6f));
			Assert.That(library.ForStop("unmeasured").CanopyWidth,Is.EqualTo(generic.CanopyWidth));
		}

		[Test] public void ReversedAndConcaveOutlinesKeepValidFiniteMeshes()
		{
			var concave=new[] {new Vector3(-14,0,-5),new Vector3(14,0,-5),new Vector3(14,0,5),new Vector3(3,0,5),new Vector3(3,0,1),new Vector3(-3,0,1),new Vector3(-3,0,5),new Vector3(-14,0,5)};
			foreach(var points in new[] {Outline.Reverse().ToArray(),concave})
			{
				var kit=LandingKit.Build(_root.transform,points,Faces,new LandingStyle());
				Assert.That(kit.CanopyFrames,Is.GreaterThanOrEqualTo(3));
				foreach(var v in kit.FloatingMesh.vertices)Assert.That(float.IsNaN(v.x)||float.IsNaN(v.y)||float.IsNaN(v.z),Is.False);
			}
		}

		[Test] public void ReleasingOneKitPreservesTheOtherSharedRampAndReleasingBothFreesIt()
		{
			var a=Build();var b=Build();var mesh=b.GangwayMesh;
			Object.DestroyImmediate(a.gameObject);Assert.That(mesh!=null,Is.True);Assert.That(b.GangwayMesh,Is.SameAs(mesh));
			Object.DestroyImmediate(b.gameObject);Assert.That(mesh==null,Is.True);
		}

		[Test] public void BadOutlineOrDimensionsAreRejectedBeforeCreatingObjects()
		{
			Assert.Throws<ArgumentException>(() => LandingKit.Build(_root.transform, new[] { Vector3.zero, Vector3.right }, Faces, new LandingStyle()));
			Assert.Throws<ArgumentException>(() => LandingKit.Build(_root.transform, Outline, Faces, new LandingStyle { Freeboard = float.NaN }));
			Assert.Throws<ArgumentException>(() => LandingKit.Build(_root.transform, Outline, Faces, new LandingStyle { FenderSpacing = 0 }));
			Assert.That(_root.transform.childCount, Is.Zero);
		}
	}
}
