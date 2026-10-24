using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FerrySim.Controls.Profiles.Editor
{
	public static class InputCalibrationSceneBuilder
	{
		public const string ScenePath = "Assets/FerrySim/Input/Profiles/Scenes/InputCalibrationDev.unity";
		[MenuItem("FerrySim/Input/Rebuild Calibration Dev Scene")]
		public static void Build()
		{
			var previousSetup = EditorSceneManager.GetSceneManagerSetup();
			try
			{
				var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
				var camera = new GameObject("Camera").AddComponent<Camera>();
				camera.clearFlags = CameraClearFlags.SolidColor;
				camera.backgroundColor = new Color(14f / 255f, 19f / 255f, 24f / 255f);
				var controls = new GameObject("Helm input"); controls.SetActive(false);
				controls.AddComponent<HelmInputController>().Actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>("Assets/FerrySim/Input/FerrySimControls.inputactions");
				controls.SetActive(true);
				new GameObject("Input calibration (UI Toolkit)").AddComponent<CalibrationScreen>();
				if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("Could not save " + ScenePath);
				Debug.Log("InputCalibrationSceneBuilder: wrote " + ScenePath);
			}
			finally
			{
				EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
			}
		}
	}
}
