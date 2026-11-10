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
		[Serializable] private class Control
		{
			public string id, kind;
			public VectorNumber pivot, travel;
			public float[] axis, normal;
			public int lamp;
		}
		[Serializable] private class Screen { public string id; public VectorNumber centre, focus_eye; public Number tilt, yaw; }
		[Serializable] private class Layout { public int schema_version; public Values values; public Control[] controls; public Screen[] screens; }

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
				importer.generateSecondaryUV=false; // UV1 carries exact lamp indices, not lightmap coordinates.
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
					var layout=JsonUtility.FromJson<Layout>(File.ReadAllText(file));
					if(layout.schema_version!=2 || layout.controls==null || layout.screens==null)
						throw new InvalidOperationException("Expected wheelhouse layout v2 with controls and screens: "+id);
					var v=layout.values;
					var hullFile=Path.GetFullPath(Path.Combine(Application.dataPath,"../../art/blender/data/"+id+"-hull.json"));
					var datums=File.Exists(hullFile)?JsonUtility.FromJson<Layout>(File.ReadAllText(hullFile)).values:v;
					var floor=datums.upper_deck_height.value;var front=datums.wheelhouse_fore_z.value;
					CheckMount(root.transform,"Eye",v.eye_seated.value,floor,front);
					CheckMount(root.transform,"EyeStanding",v.eye_standing.value,floor,front);
					var map=root.AddComponent<WheelhouseControlMap>();
					var ids=new HashSet<string>(StringComparer.Ordinal);var lampIndices=new HashSet<int>();
					foreach(var control in layout.controls)
					{
						if(!ids.Add(control.id)) throw new InvalidOperationException("Duplicate control: "+control.id);
						var mount=CheckMount(root.transform,control.id,control.pivot.value,floor,front);
						if(Quaternion.Angle(mount.localRotation,Quaternion.identity)>.01f)
							throw new InvalidOperationException("Control rest rotation must be identity: "+control.id);
						var lamp=control.kind=="lamp"?control.lamp:-1;
						if(control.kind=="lamp" && (lamp<0 || !lampIndices.Add(lamp)))
							throw new InvalidOperationException("Invalid lamp index: "+control.id);
						map.Controls.Add(new WheelhouseControlMap.ControlMount {
							Id=control.id,Kind=control.kind,Mount=mount,
							Axis=Direction(control.axis,Vector3.up),Normal=Direction(control.normal,Vector3.up),
							Travel=control.travel==null?Vector2.zero:new Vector2(control.travel.value[0],control.travel.value[1]),LampIndex=lamp
						});
					}
					foreach(var screen in layout.screens)
					{
						if(!ids.Add(screen.id)) throw new InvalidOperationException("Duplicate screen: "+screen.id);
						var mount=CheckMount(root.transform,screen.id,screen.centre.value,floor,front);
						CheckMount(root.transform,screen.id+"FocusEye",screen.focus_eye.value,floor,front);
						var expected=Quaternion.AngleAxis(screen.yaw?.value??0,Vector3.up)*Quaternion.AngleAxis(screen.tilt.value,Vector3.right);
						if(Quaternion.Angle(mount.localRotation,expected)>.01f)
							throw new InvalidOperationException("Interior FBX screen orientation mismatch: "+screen.id);
					}
					if(root.GetComponentInChildren<Collider>(true)!=null || root.GetComponentInChildren<Rigidbody>(true)!=null)
						throw new InvalidOperationException("Interior must remain render only");
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
							if(name.EndsWith("Screen",StringComparison.Ordinal))
							{
								material.EnableKeyword("_EMISSION");
								material.SetColor("_EmissionColor",material.GetColor("_BaseColor")*.18f);
							}
							material.enableInstancing=true;EditorUtility.SetDirty(material);slots[i]=material;
						}
						renderer.sharedMaterials=slots;
					}
					PrefabUtility.SaveAsPrefabAsset(root,output);
					var triangles=0;foreach(var mesh in root.GetComponentsInChildren<MeshFilter>()) triangles+=mesh.sharedMesh.triangles.Length/3;
					if(triangles>150000) throw new InvalidOperationException("Interior triangle budget exceeded");
					Debug.Log("[WheelhouseInterior] "+id+" triangles="+triangles+" renderers="+root.GetComponentsInChildren<Renderer>().Length+" controls="+map.Controls.Count);
				}
				finally { Object.DestroyImmediate(root); }
			}
			AssetDatabase.SaveAssets();AssetDatabase.Refresh();
		}

		private static Vector3 Direction(float[] value,Vector3 fallback)
		{
			if(value==null) return fallback;
			if(value.Length!=3) throw new InvalidOperationException("Direction must have three components");
			var result=new Vector3(value[0],value[1],value[2]);
			if(float.IsNaN(result.sqrMagnitude) || Mathf.Abs(result.sqrMagnitude-1)>.0001f)
				throw new InvalidOperationException("Direction must be a finite unit vector");
			return result;
		}

		private static Transform CheckMount(Transform root,string name,float[] p,float floor,float front)
		{
			var mount=root.Find(name);var expected=new Vector3(p[0],p[1]+floor,p[2]+front);
			if(mount==null || Vector3.Distance(mount.localPosition,expected)>.001f)
				throw new InvalidOperationException("Interior FBX mount mismatch: "+name+" expected "+expected.ToString("F6")+" got "+(mount!=null?mount.localPosition.ToString("F6"):"missing"));
			// Preserve imported model coordinates; do not replace them with the exterior's eye estimate.
			return mount;
		}
	}
}
