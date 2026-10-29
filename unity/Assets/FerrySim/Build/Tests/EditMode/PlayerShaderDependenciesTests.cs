using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FerrySim.Build.Tests
{
	/// <summary>Shaders found only by runtime name disappear from a harbor-only player unless an asset retains them.</summary>
	public class PlayerShaderDependenciesTests
	{
		[TestCase("FerrySim/SolidColour"), TestCase("FerrySim/HarborLabel")]
		public void RuntimeNavigationShaderHasAnExplicitPlayerDependency(string name)
		{
			var shader = Shader.Find(name);
			Assert.That(shader, Is.Not.Null, "source shader exists in the Editor");
			var assets = AssetDatabase.GetAllAssetPaths().Where(path => path.StartsWith("Assets/FerrySim/Harbor/Player/Resources/", StringComparison.Ordinal)).ToArray();
			var dependencies = AssetDatabase.GetDependencies(assets);
			Assert.That(dependencies, Does.Contain(AssetDatabase.GetAssetPath(shader)), "the installed harbor must retain this runtime-only lookup");
		}
	}
}
