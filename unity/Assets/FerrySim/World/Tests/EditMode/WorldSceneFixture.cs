using System.Linq;
using FerrySim.World.EditorTools;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FerrySim.World.Tests.EditMode
{
	/// <summary>Opens World/Scenes/LowerEastRiver.unity additively for a test and closes it afterwards.</summary>
	public sealed class WorldSceneFixture
	{
		private Scene _scene;
		private Scene _previousActive;

		public Scene Scene => _scene;

		public void Open()
		{
			_previousActive = SceneManager.GetActiveScene();
			_scene = EditorSceneManager.OpenScene(SceneBuilder.ScenePath, OpenSceneMode.Additive);
			Assert.That(_scene.IsValid(), Is.True, SceneBuilder.ScenePath + " did not open; run FerrySim > World > Rebuild All.");
			SceneManager.SetActiveScene(_scene);
		}

		public void Close()
		{
			if (_previousActive.IsValid())
			{
				SceneManager.SetActiveScene(_previousActive);
			}

			if (_scene.IsValid())
			{
				EditorSceneManager.CloseScene(_scene, true);
			}
		}

		public T Find<T>() where T : Component
		{
			var found = _scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<T>(true)).ToList();
			Assert.That(found.Count, Is.EqualTo(1), $"expected exactly one {typeof(T).Name} in the scene");
			return found[0];
		}

		public T[] FindAll<T>() where T : Component =>
			_scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<T>(true)).ToArray();
	}
}
