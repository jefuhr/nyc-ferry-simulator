using FerrySim.World.EditorTools;
using NUnit.Framework;

namespace FerrySim.World.Tests.EditMode
{
	/// <summary>Guards identity across rebuilt instances, repeated child names under different parents,
	/// stable names for fresh ids and accidental rewriting of external prefab/mesh/script references.</summary>
	public sealed class WorldFileIdsTests
	{
		[Test]
		public void ExistingHierarchy_KeepsIds_DespiteNewTemporaryIds()
		{
			var original = Hierarchy(1001, 1002, 1003, 1004);
			var rebuilt = Hierarchy(2001, 2002, 2003, 2004);
			Assert.That(WorldFileIds.Canonicalize(rebuilt, original), Is.EqualTo(original));
		}

		[Test]
		public void NewHierarchy_HashesNames_IndependentlyOfTemporaryIds()
		{
			Assert.That(WorldFileIds.Canonicalize(Hierarchy(1001, 1002, 1003, 1004), ""),
				Is.EqualTo(WorldFileIds.Canonicalize(Hierarchy(2001, 2002, 2003, 2004), "")));
		}

		[Test]
		public void ExternalAssetFileIds_AreNeverRewritten()
		{
			var yaml = Hierarchy(1001, 1002, 1003, 1004);
			var result = WorldFileIds.Canonicalize(yaml, "");
			Assert.That(result, Does.Contain("m_External: {fileID: 1001, guid: abcdef, type: 3}"));
		}

		[Test]
		public void RepeatedChildNames_AreMatchedByTheirParentHierarchy()
		{
			var first = Hierarchy(1001, 1002, 1003, 1004).Replace("m_Name: Root", "m_Name: First");
			var second = Hierarchy(3001, 3002, 3003, 3004).Replace("m_Name: Root", "m_Name: Second").Replace("%YAML 1.1\n", "");
			var rebuiltFirst = Hierarchy(2001, 2002, 2003, 2004).Replace("m_Name: Root", "m_Name: First");
			var rebuiltSecond = Hierarchy(4001, 4002, 4003, 4004).Replace("m_Name: Root", "m_Name: Second").Replace("%YAML 1.1\n", "");
			Assert.That(WorldFileIds.Canonicalize(rebuiltFirst + rebuiltSecond, first + second), Is.EqualTo(first + second));
		}

		[Test]
		public void PrefabOrdering_KeepsComponentsBesideTheirGameObject()
		{
			var original = Hierarchy(1001, 1004, 1002, 1003);
			Assert.That(WorldFileIds.Canonicalize(original, original, groupComponents: true), Is.EqualTo(original));
		}

		private static string Hierarchy(int root, int rootTransform, int child, int childTransform) =>
			$"%YAML 1.1\n--- !u!1 &{root}\nGameObject:\n  m_Name: Root\n  m_Component:\n  - component: {{fileID: {rootTransform}}}\n" +
			$"--- !u!4 &{rootTransform}\nTransform:\n  m_GameObject: {{fileID: {root}}}\n  m_Father: {{fileID: 0}}\n  m_Children:\n  - {{fileID: {childTransform}}}\n" +
			$"--- !u!1 &{child}\nGameObject:\n  m_Name: Hull\n  m_Component:\n  - component: {{fileID: {childTransform}}}\n  m_External: {{fileID: 1001, guid: abcdef, type: 3}}\n" +
			$"--- !u!4 &{childTransform}\nTransform:\n  m_GameObject: {{fileID: {child}}}\n  m_Father: {{fileID: {rootTransform}}}\n";
	}
}
