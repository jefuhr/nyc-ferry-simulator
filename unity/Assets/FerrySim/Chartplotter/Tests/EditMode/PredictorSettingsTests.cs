using NUnit.Framework;

namespace FerrySim.Chartplotter.Tests.EditMode
{
	// Failure mode: the chart still predicts six minutes despite the saved display setting's two-minute default.
	public class PredictorSettingsTests
	{
		[Test]
		public void DefaultVectorIsTwoMinutes()
		{
			var view = new ChartplotterView();
			try { Assert.That(view.VectorMinutes, Is.EqualTo(2)); }
			finally { view.Canvas.Dispose(); view.Targets.Dispose(); }
		}
	}
}
