using NUnit.Framework;
using UnityEngine;

namespace FerrySim.Prompts.Tests.EditMode
{
	/// <summary>
	/// The placement solver on its own, in panel pixels. Failure modes guarded: overlapping plates in any
	/// arrangement, plates leaving the screen, more than the cap shown, and a plate that flips between two spots,
	/// or two plates that swap, when an anchor or a priority wobbles across a threshold from frame to frame.
	/// </summary>
	public class KeybindDeclutterTests
	{
		private static readonly Vector2 Screen = new Vector2(1280f, 720f);
		private static readonly Vector2 Plate = new Vector2(60f, 40f);
		private const float Leader = 18f;
		private const float Margin = 4f;

		[Test]
		public void ACollidingPlate_IsNudged_AndDoesNotFlipBackAtTheEdgeOfTheCollision()
		{
			var solver = new KeybindDeclutter(4);
			void Solve(float bx)
			{
				solver.Set(0, new Vector2(500f, 400f), Plate, 0.1f);
				solver.Set(1, new Vector2(bx, 400f), Plate, 0.2f);
				Assert.That(solver.Solve(2, Screen, Leader, 12, Margin), Is.EqualTo(2));
				Assert.That(solver.Placed(0).Overlaps(solver.Placed(1)), Is.False);
				Assert.That(solver.Slot(0), Is.Zero, "the plate nearer the centre keeps its own spot");
			}

			Solve(550f);
			Assert.That(solver.Slot(1), Is.Not.Zero, "overlapping at home: nudged");
			Solve(562f);
			Assert.That(solver.Slot(1), Is.Not.Zero, "2 px clear of home is not enough to move back");
			Solve(566f);
			Assert.That(solver.Slot(1), Is.Zero, "6 px clear: back home");
			Solve(563f);
			Assert.That(solver.Slot(1), Is.Zero, "3 px clear: stays home");
			Solve(561f);
			Assert.That(solver.Slot(1), Is.Zero, "1 px clear: still home");
			Solve(558f);
			Assert.That(solver.Slot(1), Is.Not.Zero, "overlapping again: nudged");
		}

		[Test]
		public void AtTheCap_TheShownSetHolds_UntilANewcomerIsClearlyNearer()
		{
			var solver = new KeybindDeclutter(16);
			void Solve(float lastPriority)
			{
				for (var i = 0; i < 12; i++)
				{
					solver.Set(i, new Vector2(100f + i * 90f, 300f), Plate, 0.1f + i * 0.01f);
				}

				solver.Set(12, new Vector2(640f, 600f), Plate, lastPriority);
				Assert.That(solver.Solve(13, Screen, Leader, 12, Margin), Is.EqualTo(12));
			}

			Solve(0.5f);
			Assert.That(solver.Shown(12), Is.False, "13th nearest: not shown");
			Assert.That(solver.Shown(11), Is.True);
			Solve(0.205f);
			Assert.That(solver.Shown(12), Is.False, "now just nearer than the 12th (0.21): the shown set holds");
			Assert.That(solver.Shown(11), Is.True);
			Solve(0.12f);
			Assert.That(solver.Shown(12), Is.True, "clearly nearer: it takes the place of the farthest");
			Assert.That(solver.Shown(11), Is.False);
			Solve(0.205f);
			Assert.That(solver.Shown(12), Is.True, "and keeps it while it stays about as near");
		}

		[Test]
		public void TwoCollidingPlates_SwapSpots_OnlyWhenTheOrderChangesClearly()
		{
			var solver = new KeybindDeclutter(4);
			void Solve(float bPriority)
			{
				solver.Set(0, new Vector2(500f, 400f), Plate, 0.10f);
				solver.Set(1, new Vector2(520f, 400f), Plate, bPriority);
				solver.Solve(2, Screen, Leader, 12, Margin);
				Assert.That(solver.Placed(0).Overlaps(solver.Placed(1)), Is.False);
			}

			Solve(0.12f);
			Assert.That(solver.Slot(0), Is.Zero);
			Solve(0.09f);
			Assert.That(solver.Slot(0), Is.Zero, "B is only a little nearer the centre: A keeps its spot");
			Assert.That(solver.Slot(1), Is.Not.Zero);
			Solve(0.02f);
			Assert.That(solver.Slot(1), Is.Zero, "B is clearly nearer: it takes its own spot");
			Assert.That(solver.Slot(0), Is.Not.Zero);
		}

		[Test]
		public void InAnyArrangement_ShownPlatesNeverOverlap_StayOnScreen_AndRespectTheCap()
		{
			var solver = new KeybindDeclutter(24);
			var random = new System.Random(20261110);
			for (var trial = 0; trial < 300; trial++)
			{
				var count = random.Next(1, 25);
				var spread = trial % 3 == 0 ? 120f : 600f;
				for (var i = 0; i < count; i++)
				{
					var anchor = new Vector2(640f + ((float)random.NextDouble() - 0.5f) * spread * 2f, 360f + ((float)random.NextDouble() - 0.5f) * spread);
					anchor = Vector2.Max(Vector2.zero, Vector2.Min(Screen, anchor));
					var size = new Vector2(30f + (float)random.NextDouble() * 120f, 30f + (float)random.NextDouble() * 20f);
					solver.Set(i, anchor, size, (float)random.NextDouble());
				}

				var shown = solver.Solve(count, Screen, Leader, 12, Margin);
				Assert.That(shown, Is.LessThanOrEqualTo(12));
				var counted = 0;
				for (var a = 0; a < count; a++)
				{
					if (!solver.Shown(a))
					{
						continue;
					}

					counted++;
					var rect = solver.Placed(a);
					Assert.That(rect.xMin, Is.GreaterThanOrEqualTo(Margin - 1e-3f), $"trial {trial}: {rect}");
					Assert.That(rect.yMin, Is.GreaterThanOrEqualTo(Margin - 1e-3f), $"trial {trial}: {rect}");
					Assert.That(rect.xMax, Is.LessThanOrEqualTo(Screen.x - Margin + 1e-3f), $"trial {trial}: {rect}");
					Assert.That(rect.yMax, Is.LessThanOrEqualTo(Screen.y - Margin + 1e-3f), $"trial {trial}: {rect}");
					for (var b = a + 1; b < count; b++)
					{
						if (solver.Shown(b))
						{
							Assert.That(rect.Overlaps(solver.Placed(b)), Is.False, $"trial {trial}: {a} {rect} and {b} {solver.Placed(b)}");
						}
					}
				}

				Assert.That(counted, Is.EqualTo(shown));
			}
		}

		[Test]
		public void APlate_DoesNotCoverThePivotOfAPlatePlacedBeforeIt()
		{
			// A column of buttons (the horn and the alarm buttons): B's home plate, above B, would sit on A's pivot dot,
			// so the reader could not tell which control A's plate belongs to (seen in the first captures).
			var solver = new KeybindDeclutter(4);
			solver.Set(0, new Vector2(600f, 400f), Plate, 0.1f);
			solver.Set(1, new Vector2(600f, 450f), Plate, 0.2f);
			Assert.That(solver.Solve(2, Screen, Leader, 12, Margin), Is.EqualTo(2));
			Assert.That(solver.Slot(0), Is.Zero, "the nearer plate keeps its place");
			Assert.That(solver.Slot(1), Is.Not.Zero, "the other moves off A's pivot");
			var pivot = new Rect(600f - KeybindDeclutter.PivotRadius, 400f - KeybindDeclutter.PivotRadius, 2f * KeybindDeclutter.PivotRadius, 2f * KeybindDeclutter.PivotRadius);
			Assert.That(solver.Placed(1).Overlaps(pivot), Is.False, $"{solver.Placed(1)} covers A's pivot");
			for (var frame = 0; frame < 5; frame++)
			{
				var slot = solver.Slot(1);
				solver.Set(0, new Vector2(600f, 400f), Plate, 0.1f);
				solver.Set(1, new Vector2(600f, 450f), Plate, 0.2f);
				solver.Solve(2, Screen, Leader, 12, Margin);
				Assert.That(solver.Slot(1), Is.EqualTo(slot), "and stays put");
			}
		}

		[Test]
		public void TheNearestPlate_IsAlwaysShownAtHome()
		{
			var solver = new KeybindDeclutter(8);
			for (var i = 0; i < 8; i++)
			{
				solver.Set(i, new Vector2(640f + i * 3f, 360f + i * 2f), Plate, 0.5f - i * 0.05f);
			}

			solver.Solve(8, Screen, Leader, 12, Margin);
			Assert.That(solver.Shown(7), Is.True);
			Assert.That(solver.Slot(7), Is.Zero);
			var home = solver.Placed(7);
			Assert.That(home.center.x, Is.EqualTo(640f + 21f).Within(1e-3f));
			Assert.That(home.yMax, Is.EqualTo(360f + 14f - Leader).Within(1e-3f));
		}
	}
}
