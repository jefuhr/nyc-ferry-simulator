using System.Collections;
using FerrySim.Core.Contracts;
using FerrySim.Core.Navigation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Chartplotter.Tests.PlayMode
{
	// Failure modes: only own ship changes horizon, degrees mistaken for radians, port sign reversed, or mode ignored.
	public class PredictorPlayModeTests
	{
		[UnityTest] public IEnumerator BothVectorsScale_AndCurvedPointsMatchCore()
		{
			using var rig = new PlotterRig(1024, 576, new Rect(0, 0, 1280, 720));
			rig.Own.Place(0, 0, 0, 0, 6);
			rig.Targets.Add("target", 12345, 100, 100, 3, 0);
			for (var i = 0; i < 4; i++) yield return null;
			var own = rig.View.Targets.OwnVectorPixels;
			var target = rig.View.Targets.TargetVectorPixels(0);
			rig.View.VectorMinutes = 4;
			yield return null;
			Assert.That(rig.View.Targets.OwnVectorPixels, Is.EqualTo(own * 2).Within(.001));
			Assert.That(rig.View.Targets.TargetVectorPixels(0), Is.EqualTo(target * 2).Within(.001));
			Assert.That(rig.View.VectorText, Does.Contain("4 MIN"));
			var expected = new HorizontalVector[33];
			var actual = new HorizontalVector[33];
			foreach (var rate in new[] { 0.0, -.01, .01 })
			{
				var state = VesselState.Planar(0, 10, 20, .4, 3, 1, rate);
				var velocity = state.GroundVelocity;
				var speed = velocity.Magnitude;
				var course = System.Math.Atan2(velocity.East, velocity.North);
				rig.View.Predictor = PredictorMode.Curved;
				rig.View.SamplePrediction(state, actual);
				PathPredictor.Sample(new HorizontalVector(10, 20), speed, course, rate, 240, expected);
				CollectionAssert.AreEqual(expected, actual);
				rig.Own.State = state;
				yield return null;
				Assert.That(rig.View.Targets.OwnPredictionPointCount, Is.EqualTo(33));
				for (var i = 0; i < expected.Length; i++)
				{
					rig.View.CurrentView.ToScreen(expected[i].North, expected[i].East, out var x, out var y);
					Assert.That(rig.View.Targets.OwnPredictionPoint(i).x, Is.EqualTo(x).Within(.001));
					Assert.That(rig.View.Targets.OwnPredictionPoint(i).y, Is.EqualTo(y).Within(.001));
				}
				if (rate == 0)
				{
					rig.View.Predictor = PredictorMode.Straight;
					rig.View.SamplePrediction(state, actual);
					CollectionAssert.AreEqual(expected, actual);
				}
			}
		}
	}
}
