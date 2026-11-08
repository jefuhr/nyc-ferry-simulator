using System;
using System.Collections.Generic;
using System.IO;
using FerrySim.World;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.Vessels.Editor
{
	/// <summary>Imports only Interior.fbx/prefabs. Exterior assets and traffic stay outside this author.</summary>
	public static class WheelhouseInteriorImporter
	{
		private const string Folder="Assets/FerrySim/Vessels/";
		[Serializable] private class Number { public float value; }
		[Serializable] private class VectorNumber { public float[] value; }
		[Serializable] private class Values { public Number lever_travel; public Number helm_travel; public VectorNumber eye_seated; public VectorNumber eye_standing; public Number upper_deck_height; public Number wheelhouse_fore_z; }
		[Serializable] private class Layout { public Values values; }

		[MenuItem("FerrySim/Vessels/Rebuild Wheelhouse Interiors")]
		public static void Build()
		{
			AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
			foreach(var id in new[]{"k-class","t-class"})
			{
				var path=Folder+"Models/"+id+"/Interior.fbx";
				var importer=(ModelImporter)AssetImporter.GetAtPath(path);
				if(importer == null) throw new InvalidOperationException("Generate "+path+" with art/blender/interior.py first.");
				importer.globalScale=1;importer.useFileScale=true;importer.bakeAxisConversion=true;
				importer.importAnimation=false;importer.importCameras=false;importer.importLights=false;
				importer.addCollider=false;importer.isReadable=true;importer.meshCompression=ModelImporterMeshCompression.Off;
				importer.materialImportMode=ModelImporterMaterialImportMode.ImportStandard;
				importer.SaveAndReimport();
				var output=Folder+"Resources/FerrySimVessels/"+id+"/Interior.prefab";
				var root=new GameObject(id+" Interior");
				try
				{
					var imported=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path),root.transform,false);
					// Keep animated mesh children under their pivots; lift only the FBX's top-level children.
					while(imported.transform.childCount>0) imported.transform.GetChild(0).SetParent(root.transform,true);
					Object.DestroyImmediate(imported);
					var file=Path.GetFullPath(Path.Combine(Application.dataPath,"../../art/blender/data/"+id+"-wheelhouse.json"));
					var v=JsonUtility.FromJson<Layout>(File.ReadAllText(file)).values;
					var hullFile=Path.GetFullPath(Path.Combine(Application.dataPath,"../../art/blender/data/"+id+"-hull.json"));
					var datums=File.Exists(hullFile)?JsonUtility.FromJson<Layout>(File.ReadAllText(hullFile)).values:v;
					CheckEye(root.transform,"Eye",v.eye_seated.value,datums.upper_deck_height.value,datums.wheelhouse_fore_z.value);
					CheckEye(root.transform,"EyeStanding",v.eye_standing.value,datums.upper_deck_height.value,datums.wheelhouse_fore_z.value);
					var view=root.AddComponent<WheelhouseControlsView>();view.ClassId=id;
					view.EngineTravelDegrees=v.lever_travel.value;view.HelmTravelDegrees=v.helm_travel.value;
					foreach(var node in root.GetComponentsInChildren<Transform>()) node.gameObject.layer=LayerMask.NameToLayer("Vessel");
					// Materials live as owned prefab subassets, never in the exterior worker's Materials folder.
					PrefabUtility.SaveAsPrefabAsset(root,output);
					var materials=new Dictionary<string,Material>();
					foreach(var asset in AssetDatabase.LoadAllAssetsAtPath(output)) if(asset is Material material) materials[material.name]=material;
					foreach(var renderer in root.GetComponentsInChildren<Renderer>())
					{
						var slots=renderer.sharedMaterials;
						for(var i=0;i<slots.Length;i++)
						{
							var src=slots[i];var name=src.name;
							if(!materials.TryGetValue(name,out var material))
							{
								material=new Material(Shader.Find("Universal Render Pipeline/Lit")){name=name};
								AssetDatabase.AddObjectToAsset(material,output);materials.Add(name,material);
							}
							material.SetColor("_BaseColor",src.HasProperty("_Color")?src.GetColor("_Color"):src.color);
							material.SetFloat("_Metallic",name=="Trim"?.75f:0);
							material.SetFloat("_Smoothness",name=="Trim"?.7f:.35f);
							material.enableInstancing=true;EditorUtility.SetDirty(material);slots[i]=material;
						}
						renderer.sharedMaterials=slots;
					}
					PrefabUtility.SaveAsPrefabAsset(root,output);
					Debug.Log("[WheelhouseInterior] "+id+" Eye="+root.transform.Find("Eye").localPosition.ToString("F4"));
				}
				finally { Object.DestroyImmediate(root); }
			}
			AssetDatabase.SaveAssets();AssetDatabase.Refresh();
		}

		private static void CheckEye(Transform root,string name,float[] p,float floor,float front)
		{
			var mount=root.Find(name);var expected=new Vector3(p[0],p[1]+floor,p[2]+front);
			if(mount==null || Vector3.Distance(mount.localPosition,expected)>.001f)
				throw new InvalidOperationException("Interior FBX eye mismatch: "+name+" expected "+expected.ToString("F6")+" got "+(mount!=null?mount.localPosition.ToString("F6"):"missing"));
			// Preserve imported model coordinates; do not replace them with the exterior's eye estimate.
		}
	}
}
