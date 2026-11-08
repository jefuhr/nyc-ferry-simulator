using System.IO;
using System.Linq;
using FerrySim.World.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.World.Landings.Tests
{
	/// <summary>Failure modes: transient meshes disappear from the prefab, duplicate ramps, lost tide parent,
	/// source diagnostics still render, or the test rebuild overwrites the shipped world.</summary>
	public sealed class LandingSliceTests
	{
		[Test] public void SliceBuilderPersistsKitMeshesAndTideBindingsInAnIsolatedPrefab()
		{
			var output=AssetDatabase.GenerateUniqueAssetPath("Assets/FerrySim/World/Landings/Tests/Generated");
			var shipped=File.ReadAllBytes(GreyboxBuilder.PrefabPath);
			GameObject instance=null;
			try
			{
				var report=GreyboxBuilder.Build(PipelineData.Load(),output);
				Assert.That(report.Errors,Is.Empty);
				var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(output+"/Greybox/LowerEastRiverGreybox.prefab");
				Assert.That(prefab,Is.Not.Null);
				instance=Object.Instantiate(prefab);
				var kits=instance.GetComponentsInChildren<LandingKit>();
				Assert.That(kits.Length,Is.GreaterThanOrEqualTo(11));
				foreach(var kit in kits)
				{
					Assert.That(AssetDatabase.Contains(kit.FloatingMesh),Is.True,"floating mesh persisted");
					Assert.That(AssetDatabase.Contains(kit.GangwayMesh),Is.True,"shared ramp persisted");
					Assert.That(kit.GetComponentsInChildren<Renderer>().Length,Is.EqualTo(3));
					var legacy=kit.transform.parent.Find("Hull");
					Assert.That(legacy.GetComponent<Renderer>().enabled,Is.False,"source diagnostics do not cover the kit");
					var binding=new SerializedObject(kit).FindProperty("sliceTideParent").objectReferenceValue as Transform;
					Assert.That(binding,Is.Not.Null,"prefab retained its tide parent");
					var original=binding.localPosition;var fixedAtZero=kit.FixedPiles.position;
					binding.localPosition=original+Vector3.up;
					kit.SendMessage("LateUpdate");
					Assert.That(Vector3.Distance(fixedAtZero,kit.FixedPiles.position),Is.LessThan(.001f));
					binding.localPosition=original;kit.SendMessage("LateUpdate");
				}
				Assert.That(kits.Select(k=>k.GangwayMesh).Distinct().Count(),Is.LessThan(kits.Length),"equal ramps share baked assets");
				Assert.That(File.ReadAllBytes(GreyboxBuilder.PrefabPath),Is.EqualTo(shipped),"shipped prefab remains untouched");
				TestContext.WriteLine("Isolated slice prefab: "+kits.Length+" complete kits; meshes, shared ramps and tide bindings persisted.");
			}
			finally
			{
				if(instance!=null)Object.DestroyImmediate(instance);
				AssetDatabase.DeleteAsset(output); // Only this test's unique generated folder.
			}
		}
	}
}
