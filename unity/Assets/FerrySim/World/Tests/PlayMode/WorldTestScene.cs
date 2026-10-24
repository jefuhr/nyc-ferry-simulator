using System.Collections;
using System.IO;
using System.Linq;
using CesiumForUnity;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

namespace FerrySim.World.Tests.PlayMode
{
	/// <summary>Loads World/Scenes/LowerEastRiver.unity in play mode and finds its parts.</summary>
	public sealed class WorldTestScene
	{
		public const string ScenePath = "Assets/FerrySim/World/Scenes/LowerEastRiver.unity";

		// Application.dataPath is <repo>/unity/Assets, so the repo root is two levels up.
		public static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

		public static string ArtifactDirectory => Path.Combine(RepoRoot, "artifacts", "unity");

		public CesiumGeoreference Georeference { get; private set; }

		public WorldFrame Frame { get; private set; }

		public WaterSurface Water { get; private set; }

		public SunController Sun { get; private set; }

		public CameraRig Rig { get; private set; }

		public Camera Camera { get; private set; }

		public GeoreferenceCheck Check { get; private set; }

		public LandingBarge Barge(string stopId) => Object.FindObjectsByType<LandingBarge>(FindObjectsSortMode.None).Single(b => b.StopId == stopId);

		public BridgeGreybox Bridge(string name) => Object.FindObjectsByType<BridgeGreybox>(FindObjectsSortMode.None).Single(b => b.BridgeName == name);

		public IEnumerator Load()
		{
#if UNITY_EDITOR
			yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
#else
			Assert.Fail("World PlayMode tests load the scene through the editor and only run in the editor.");
#endif
			yield return null;
			Georeference = Object.FindFirstObjectByType<CesiumGeoreference>();
			Frame = Object.FindFirstObjectByType<WorldFrame>();
			Water = Object.FindFirstObjectByType<WaterSurface>();
			Sun = Object.FindFirstObjectByType<SunController>();
			Rig = Object.FindFirstObjectByType<CameraRig>();
			Camera = Rig != null ? Rig.GetComponent<Camera>() : null;
			Check = Object.FindFirstObjectByType<GeoreferenceCheck>();
			Assert.That(Georeference, Is.Not.Null, "scene did not load: " + ScenePath);
			Assert.That(Frame, Is.Not.Null);
			Assert.That(Water, Is.Not.Null);
			Assert.That(Sun, Is.Not.Null);
			Assert.That(Rig, Is.Not.Null);
			Assert.That(Check, Is.Not.Null);
		}

		/// <summary>Unity position of a point given in world-frame coordinates (x east, y up, z north).</summary>
		public Vector3 ToUnity(Vector3 worldFrameLocal) => Frame.transform.TransformPoint(worldFrameLocal);

		/// <summary>World-frame point on the water surface at the current level, raised by <paramref name="above"/> metres.</summary>
		public Vector3 OnWater(double north, double east, float above) =>
			new Vector3((float)east, (float)Water.SurfaceUp(north, east) + above, (float)north);

		/// <summary>Places the free-fly camera (in world-frame coordinates) and gives the origin shift a few frames to settle.</summary>
		public IEnumerator LookFrom(Vector3 eyeLocal, Vector3 targetLocal)
		{
			Rig.CurrentMode = CameraRig.Mode.FreeFly;
			Rig.FreeFly.SetPoseLookingAt(ToUnity(eyeLocal), ToUnity(targetLocal));
			for (var i = 0; i < 4; i++)
			{
				yield return null;
			}
		}
	}
}
