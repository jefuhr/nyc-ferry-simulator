using System;
using System.IO;
using System.Globalization;
using System.Text;
using UnityEngine.Profiling;
using System.Linq;
using System.Security.Cryptography;
using CesiumForUnity;
using FerrySim.Core.Contracts;
using FerrySim.Core.Dynamics;
using FerrySim.Core.Serialization;
using FerrySim.Core.World;
using FerrySim.Sim;
using FerrySim.Slice;
using FerrySim.World.HarborNavigation;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.World.Landings.Tests
{
	/// <summary>Failure modes: stops omitted, render geometry reaches contacts, tide/origin shift divergence,
	/// or visual construction changes a contact-enabled simulation's bitwise state trace.</summary>
	public class LandingHarborTests
	{
		private static string Repo => Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
		private static string Runtime => Path.Combine(Repo, "data/processed/harbor/runtime");
		private GameObject _root;
		private HarborRuntimePack _pack;
		private CesiumGeoreference _geo;
		private HarborNavigationWorld _world;
		[SetUp] public void Setup()
		{
			_pack = HarborRuntimePackJson.Load(Runtime);
			_root = new GameObject("landing harbor test");
			_geo = _root.AddComponent<CesiumGeoreference>(); _geo.SetOriginLongitudeLatitudeHeight(-74.006,40.703,0);
			_world = _root.AddComponent<HarborNavigationWorld>();
		}
		[TearDown] public void Cleanup() => Object.DestroyImmediate(_root);
		private void Build() => _world.Build(_pack, _geo, new Water(_pack), null);

		[Test] public void All24StopsAnd28SourceBargesReceiveACompleteKit()
		{
			Build();
			Assert.That(_pack.Stops.Count, Is.EqualTo(24));
			Assert.That(_world.Barges.Count, Is.EqualTo(28));
			foreach (var stop in _pack.Stops)
				Assert.That(_world.Barges.Any(b => b.Source.StopIds.Any(id => id.Text == stop.Id) && b.GetComponentInChildren<LandingKit>() != null), Is.True, "stop " + stop.Id);
			foreach (var barge in _world.Barges)
			{
				var kit = barge.GetComponentInChildren<LandingKit>();
				Assert.That(kit, Is.Not.Null, barge.Source.Id);
				Assert.That(kit.CanopyFrames, Is.GreaterThan(0));
				Assert.That(kit.FenderPanels, Is.GreaterThan(0));
			}
		}

		[Test] public void ContactBytesSegmentsAndStateDigestsDoNotChangeWhenVisualsAreBuiltAndTicked()
		{
			var paths = new[] { Path.Combine(Runtime,"contacts.json"), Path.Combine(Runtime,"landings.json"),
				Path.Combine(Application.dataPath,"FerrySim/Harbor/Data/landing-contact-surfaces.json"), Path.Combine(Application.dataPath,"FerrySim/Harbor/Data/landing-audit.json") };
			var hashes = paths.Select(Hash).ToArray();
			var contacts = LandingGeometry.Load(paths[0], paths[2]);
			var geometryDigest = contacts.Digest;
			var before = Simulate(contacts);
			Build();
			for (var i=0;i<50;i++) _world.Tick(i*.1,null,"87",i%2==0,0);
			var after = Simulate(contacts, i => _world.Tick(i * .01, null, "87", false, 0));
			Assert.That(after, Is.EqualTo(before));
			Assert.That(contacts.Digest, Is.EqualTo(geometryDigest));
			Assert.That(paths.Select(Hash), Is.EqualTo(hashes));
			Assert.That(_root.GetComponentsInChildren<Collider>(), Is.Empty);
			Assert.That(_world.Queries, Is.SameAs(_pack.Queries));
			TestContext.WriteLine("contact_segments="+contacts.Segments.Count+"; geometry="+geometryDigest+"; state="+before+"; contacts_sha256="+hashes[0]);
		}

		[Test] public void TideAndOriginShiftsKeepKitAttachedToTheDoubleAnchor()
		{
			Build();
			foreach (var barge in _world.Barges)
			{
				var kit = barge.GetComponentInChildren<LandingKit>(); Assert.That(kit, Is.Not.Null);
				var mesh = kit.FloatingMesh;
				_world.Tick(0,null,"87",false,0);
				var fixedAtZero = _geo.TransformUnityPositionToEarthCenteredEarthFixed((double3)(float3)kit.FixedPiles.position);
				_world.Tick(25,null,"87",false,0);
				Assert.That(barge.Anchor.Height, Is.EqualTo(new Water(_pack).EllipsoidalHeight(barge.Anchor.North,barge.Anchor.East,25)));
				var fixedBefore = _geo.TransformUnityPositionToEarthCenteredEarthFixed((double3)(float3)kit.FixedPiles.position);
				Assert.That(math.distance(fixedAtZero,fixedBefore),Is.LessThan(.02),"pile stays fixed while tide rises");
				var position = barge.GetComponent<CesiumGlobeAnchor>().positionGlobeFixed;
				_geo.SetOriginLongitudeLatitudeHeight(-73.95,40.76,0);
				_world.Tick(25,null,"87",true,0);
				Assert.That(math.distance(position,barge.GetComponent<CesiumGlobeAnchor>().positionGlobeFixed), Is.LessThan(.001));
				Assert.That(math.distance(fixedBefore,_geo.TransformUnityPositionToEarthCenteredEarthFixed((double3)(float3)kit.FixedPiles.position)), Is.LessThan(.02));
				Assert.That(kit.FloatingMesh, Is.SameAs(mesh));
			}
		}

		[Test] public void MacEditorTickCostAndAllocationAreRecordedWithoutAFrameBudgetClaim()
		{
			Build();
			for(var i=0;i<100;i++)_world.Tick(i*.01,null,"87",false,0);
			var recorder=Recorder.Get("GC.Alloc");recorder.enabled=false;recorder.FilterToCurrentThread();
			try
			{
				recorder.enabled=true;var control=new byte[123];recorder.enabled=false;
				var calibration=recorder.sampleBlockCount;GC.KeepAlive(control);
				Assert.That(calibration, Is.EqualTo(1), "one deliberate byte-array allocation calibrates GC.Alloc");
				recorder.enabled=true;for(var i=0;i<1000;i++)_world.Tick(i*.01,null,"87",false,0);recorder.enabled=false;
				Assert.That(recorder.sampleBlockCount, Is.Zero);
			}
			finally {recorder.enabled=false;recorder.CollectFromAllThreads();}
			var samples=new double[1000];
			for(var i=0;i<samples.Length;i++)
			{
				var start=System.Diagnostics.Stopwatch.GetTimestamp();_world.Tick(i*.01,null,"87",false,0);
				samples[i]=(System.Diagnostics.Stopwatch.GetTimestamp()-start)*1000.0/System.Diagnostics.Stopwatch.Frequency;
			}
			var folder=Path.Combine(Repo,"artifacts/landing-models");Directory.CreateDirectory(folder);
			var csv=new StringBuilder("sample,editor_cpu_ms\n");
			for(var i=0;i<samples.Length;i++)csv.Append(i).Append(',').Append(samples[i].ToString("R",CultureInfo.InvariantCulture)).Append('\n');
			File.WriteAllText(Path.Combine(folder,"mac-editor-ticks.csv"),csv.ToString());
			var costs=new StringBuilder("barge,stop,triangles,renderer_batches,fender_panels,canopy_frames,face_estimated\n");
			foreach(var barge in _world.Barges)
			{
				var kit=barge.GetComponentInChildren<LandingKit>();Assert.That(kit,Is.Not.Null);
				costs.Append(barge.Source.Id).Append(',').Append(barge.Source.StopIds[0].Text).Append(',').Append(kit.TriangleCount).Append(',')
					.Append(kit.GetComponentsInChildren<Renderer>().Length).Append(',').Append(kit.FenderPanels).Append(',').Append(kit.CanopyFrames).Append(',').Append(kit.FaceOrientationEstimated).Append('\n');
			}
			File.WriteAllText(Path.Combine(folder,"landing-costs.csv"),costs.ToString());
			Array.Sort(samples);TestContext.WriteLine("Mac editor headless Tick only: median_ms="+samples[500]+"; p95_ms="+samples[950]+"; max_ms="+samples[999]+"; GC.Alloc=0; no rendered-frame claim");
		}

		private static string Simulate(LandingGeometry contacts, Action<int> render = null)
		{
			var parameters = VesselParametersJson.Load(Path.Combine(Repo,"vessels/t-class.json"));
			var face = contacts.PrimaryFace("87");
			var segment = face.Segments[0].Segment;
			var n=segment.End.North-segment.Start.North; var e=segment.End.East-segment.Start.East; var length=Math.Sqrt(n*n+e*e);
			var outward=new HorizontalVector(-e/length,n/length);
			var initial=VesselState.Planar(0,(segment.Start.North+segment.End.North)*.5+outward.North*14,(segment.Start.East+segment.End.East)*.5+outward.East*14,Math.Atan2(-outward.East,-outward.North),.8,0);
			var sim=new VesselDynamicsSimulation(parameters,UniformEnvironment.Calm,contacts.Geometry,initial);
			var digest=new StateDigest();var activeContacts=0;
			for(var i=0;i<500;i++) {render?.Invoke(i);sim.Step(ControlInputs.Neutral);digest.Add(sim.State);activeContacts+=sim.ContactCount;}
			Assert.That(activeContacts, Is.GreaterThan(0), "state digest must exercise real contact, not just open water");
			return digest.Text;
		}
		private static string Hash(string path) {using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-","").ToLowerInvariant();}
		private sealed class Water : IWaterSurface
		{
			private readonly HarborRuntimePack _pack; public Water(HarborRuntimePack pack) {_pack=pack;}
			public double EllipsoidalHeight(double n,double e,double time) {if(!_pack.Geoid.TrySample(n,e,out var height))throw new ArgumentException("datum");return height+time*.04;}
		}
	}
}
