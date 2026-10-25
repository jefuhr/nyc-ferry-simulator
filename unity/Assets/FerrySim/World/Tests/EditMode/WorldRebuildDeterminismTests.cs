using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FerrySim.World.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace FerrySim.World.Tests.EditMode
{
	/// <summary>
	/// Guards random prefab/component/scene ids, unstable object ordering and references rewritten to
	/// the wrong asset. Runs the real builders into one disposable output folder, never the shipped assets.
	/// </summary>
	public sealed class WorldRebuildDeterminismTests
	{
		[Test]
		public void TwoRealRebuilds_ProduceIdenticalBytes()
		{
			Assert.That(PipelineData.Exists(), Is.True, "Regenerate the cached lower-east-river pipeline inputs first.");
			var output = AssetDatabase.GenerateUniqueAssetPath(GreyboxBuilder.WorldFolder + "/Tests/HealthGenerated");
			var setup = EditorSceneManager.GetSceneManagerSetup();
			try
			{
				var data = PipelineData.Load();
				GreyboxBuilder.Build(data, output);
				SceneBuilder.Build(data, output);
				var first = Snapshot(output);
				GreyboxBuilder.Build(data, output);
				SceneBuilder.Build(data, output);
				var second = Snapshot(output);
				Assert.That(second.Keys, Is.EquivalentTo(first.Keys));
				var differences = new List<string>();
				foreach (var file in first.Keys.OrderBy(path => path, StringComparer.Ordinal))
				{
					if (second[file].SequenceEqual(first[file])) continue;
					differences.Add(file);
					var artifact = Path.Combine(PipelineData.RepoRoot, "artifacts/health/determinism-diff", file.TrimStart('/'));
					Directory.CreateDirectory(Path.GetDirectoryName(artifact));
					File.WriteAllBytes(artifact + ".first", first[file]);
					File.WriteAllBytes(artifact + ".second", second[file]);
				}
				Assert.That(differences, Is.Empty, "Files changed on identical rebuild: " + string.Join(", ", differences));
				TestContext.WriteLine("Compared " + first.Count + " generated files including metadata, byte for byte.");
			}
			finally
			{
				EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
				AssetDatabase.DeleteAsset(output);
				if (setup.Any(scene => scene.isLoaded) && setup.Any(scene => scene.isActive))
					EditorSceneManager.RestoreSceneManagerSetup(setup);
			}
		}

		private static Dictionary<string, byte[]> Snapshot(string output) =>
			Directory.GetFiles(output, "*", SearchOption.AllDirectories).Concat(new[] { output + ".meta" })
				.ToDictionary(path => path.Substring(output.Length), File.ReadAllBytes, StringComparer.Ordinal);
	}
}
