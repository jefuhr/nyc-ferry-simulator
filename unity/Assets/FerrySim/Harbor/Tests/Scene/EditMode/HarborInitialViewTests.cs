using FerrySim.World.HarborTiles;
using NUnit.Framework;

namespace FerrySim.Harbor.Tests
{
	public class HarborInitialViewTests
	{
		private sealed class Layer : IHarborViewLayer
		{
			public string LayerId { get; set; } = "terrain";
			public bool Required { get; set; } = true;
			public string Failure { get; set; }
			public bool HasContent { get; set; } = true;
			public float LoadProgress { get; set; } = 100;
		}

		[Test]
		public void RequiresTenConsecutiveCompleteFramesAndActualContent()
		{
			var layer = new Layer { HasContent = false };
			var gate = new HarborInitialView(new IHarborViewLayer[] { layer });
			gate.Begin(0);
			for (var i = 0; i < 20; i++) gate.Poll(1);
			Assert.That(gate.State, Is.EqualTo(HarborViewState.Waiting), "100% without content is not a view");
			layer.HasContent = true;
			for (var i = 0; i < 9; i++) gate.Poll(2);
			Assert.That(gate.State, Is.EqualTo(HarborViewState.Waiting));
			layer.LoadProgress = 99;
			gate.Poll(3);
			layer.LoadProgress = 100;
			for (var i = 0; i < 9; i++) gate.Poll(4);
			Assert.That(gate.State, Is.EqualTo(HarborViewState.Waiting), "partial frame resets the streak");
			gate.Poll(4);
			Assert.That(gate.State, Is.EqualTo(HarborViewState.Ready));
		}

		[Test]
		public void MissingRequiredSkylineNamesThePathAndStaysFailedUntilAnotherStart()
		{
			var skyline = new Layer { LayerId = "skyline", Failure = "/data/harbor/3dtiles-buildings/tileset.json missing" };
			var gate = new HarborInitialView(new IHarborViewLayer[] { new Layer(), skyline });
			gate.Begin(0);
			gate.Poll(1);
			Assert.That(gate.State, Is.EqualTo(HarborViewState.Failed));
			Assert.That(gate.Problem, Does.Contain(skyline.Failure));
			skyline.Failure = null;
			gate.Poll(2);
			Assert.That(gate.State, Is.EqualTo(HarborViewState.Failed));
			gate.Begin(3);
			for (var i = 0; i < 10; i++) gate.Poll(4);
			Assert.That(gate.State, Is.EqualTo(HarborViewState.Ready));
		}

		[Test]
		public void TimeoutNamesTheLayerThatNeverBecameReady()
		{
			var gate = new HarborInitialView(new IHarborViewLayer[] { new Layer { LayerId = "terrain", LoadProgress = 0 } });
			gate.Begin(7);
			gate.Poll(67);
			Assert.That(gate.State, Is.EqualTo(HarborViewState.Failed));
			Assert.That(gate.Problem, Does.Contain("terrain"));
		}

		[Test]
		public void SailingNeverReentersTheInitialHoldWhenTilesReloadOrFail()
		{
			var layer = new Layer();
			var gate = new HarborInitialView(new IHarborViewLayer[] { layer });
			gate.Begin(0);
			for (var i = 0; i < 10; i++) gate.Poll(1);
			layer.LoadProgress = 0;
			layer.Failure = "later streaming failure";
			gate.Poll(100);
			Assert.That(gate.State, Is.EqualTo(HarborViewState.Ready));
		}

		[Test]
		public void OptionalLayerCannotBlockTheRequiredView()
		{
			var gate = new HarborInitialView(new IHarborViewLayer[] { new Layer(), new Layer { Required = false, Failure = "optional" } });
			gate.Begin(0);
			for (var i = 0; i < 10; i++) gate.Poll(1);
			Assert.That(gate.State, Is.EqualTo(HarborViewState.Ready));
		}
	}
}
