using NUnit.Framework;

namespace FerrySim.Controls.Tests
{
	public class BindingConflictTests
	{
		[TestCase("Helm/Horn", "Displays/ChartPalette", true)]
		[TestCase("Helm/Horn", "Alarms/Silence", true)]
		[TestCase("Helm/Horn", "Camera/BowView", true)]
		[TestCase("Helm/Horn", "FreeFly/Move", false)]
		[TestCase("Displays/ChartPalette", "FreeFly/Move", false)]
		[TestCase("Alarms/Silence", "FreeFly/Move", false)]
		[TestCase("FreeFly/Move", "Camera/BowView", true)]
		[TestCase("FreeFly/Move", "Interface/Options", true)]
		[TestCase("Helm/Horn", "Audio/Mute", true)]
		[TestCase("Camera/BowView", "Audio/Mute", true)]
		public void OnlyCoexistingMapsConflict(string first, string second, bool expected)
		{
			Assert.That(BindingConflicts.Conflicts(first, "<Keyboard>/h", second, "<Keyboard>/h"), Is.EqualTo(expected));
			Assert.That(BindingConflicts.Conflicts(second, "<Keyboard>/h", first, "<Keyboard>/h"), Is.EqualTo(expected));
			Assert.That(BindingConflicts.Conflicts(first, "<Keyboard>/h", second, "<Keyboard>/j"), Is.False);
		}
		[Test]
		public void SyntheticModifiersOverlapTheirPhysicalKeys()
		{
			Assert.That(BindingConflicts.PathsOverlap("<Keyboard>/shift", "<Keyboard>/leftShift"), Is.True);
			Assert.That(BindingConflicts.PathsOverlap("<Keyboard>/rightShift", "<Keyboard>/shift"), Is.True);
			Assert.That(BindingConflicts.PathsOverlap("<Keyboard>/leftShift", "<Keyboard>/rightShift"), Is.False);
		}
	}
}
