using FerrySim.Core.Navigation;
using FerrySim.Core.World.Chart;
using NUnit.Framework;

namespace FerrySim.Chartplotter.Tests.EditMode
{
	// Failure modes: polygon area lost in tessellation, soundings out of depth order or mis-formatted, aids classed by the
	// wrong rule or drawn outside coverage, contours not collected, and hazard kinds mixed up.
	public class ChartTessellationTests
	{
		private static ChartTessellation Build()
		{
			var runtime = SyntheticHarbor.Runtime();
			return ChartTessellation.Build(SyntheticHarbor.Chart(runtime), runtime);
		}

		[Test]
		public void Polygons_KeepTheirArea()
		{
			var chart = Build();
			Assert.That(chart.Land.Count, Is.EqualTo(2), "the block in its two cells");
			Assert.That(Area(chart.Land, 0) + Area(chart.Land, 1), Is.EqualTo(1500.0 * 1500.0 - 200.0 * 200.0).Within(1e-6), "the land block less its lake");
			Assert.That(chart.DepthAreas.Count, Is.EqualTo(5));
			var total = 0.0;
			for (var i = 0; i < chart.DepthAreas.Count; i++) total += Area(chart.DepthAreas, i);
			Assert.That(total, Is.EqualTo(4000.0 * 1000.0 + 4000.0 * 1000.0 + 4000.0 * 500.0 + 2500.0 * 1500.0 + 300.0 * 300.0).Within(1e-6));
			Assert.That(chart.DepthAreas.Value[0], Is.EqualTo(10.0), "DRVAL1 kept per item");
			Assert.That(chart.Fairways.Count, Is.EqualTo(2));
			Assert.That(chart.TriangleCount, Is.GreaterThan(10));
			Assert.That(chart.BuildMilliseconds, Is.GreaterThan(0));
		}

		private static double Area(ChartTessellation.PolygonSet set, int item)
		{
			var area = 0.0;
			var start = set.TriangleStart[item];
			for (var t = start; t < start + set.TriangleCount[item]; t += 3)
			{
				int a = set.Triangles[t], b = set.Triangles[t + 1], c = set.Triangles[t + 2];
				area += System.Math.Abs((set.East[b] - set.East[a]) * (set.North[c] - set.North[a]) - (set.East[c] - set.East[a]) * (set.North[b] - set.North[a])) * 0.5;
			}

			return area;
		}

		[Test]
		public void Soundings_SortedShallowestFirst_AndFormatted()
		{
			var chart = Build();
			var soundings = chart.Soundings;
			Assert.That(soundings.Count, Is.EqualTo(SyntheticHarbor.SoundingRows * SyntheticHarbor.SoundingColumns));
			for (var k = 1; k < soundings.ByDepth.Length; k++)
			{
				Assert.That(soundings.Depth[soundings.ByDepth[k]], Is.GreaterThanOrEqualTo(soundings.Depth[soundings.ByDepth[k - 1]]));
			}

			Assert.That(ChartTessellation.SoundingText(3.24, false), Is.EqualTo("3.2"));
			Assert.That(ChartTessellation.SoundingText(9.96, false), Is.EqualTo("10"));
			Assert.That(ChartTessellation.SoundingText(12.4, false), Is.EqualTo("12"));
			Assert.That(ChartTessellation.SoundingText(3.048, true), Is.EqualTo("10"), "10 ft");
			Assert.That(soundings.TextMetres[0], Is.Not.Null);
			Assert.That(soundings.TextFeet[0], Is.Not.Null);
		}

		[Test]
		public void Contours_Hazards_AndChannels_AreCollected()
		{
			var chart = Build();
			Assert.That(chart.ChartedContours, Is.EqualTo(new[] { 2.0, 5.0, 10.0 }));
			Assert.That(SafetyContour.Select(chart.ChartedContours, 3.0), Is.EqualTo(5.0));
			Assert.That(chart.Contours.Count, Is.EqualTo(3));
			Assert.That(chart.Contours.Value[1], Is.EqualTo(5.0));
			Assert.That(chart.Hazards.Count, Is.EqualTo(3), "two points and one area centre");
			Assert.That(chart.Hazards.Kind, Is.EquivalentTo(new[] { HazardKind.Rock, HazardKind.Wreck, HazardKind.Obstruction }));
			Assert.That(chart.HazardOutlines.Count, Is.EqualTo(1), "the obstruction area's outline");
			Assert.That(chart.ChannelLines.Count, Is.EqualTo(1), "the recommended track");
			Assert.That(chart.FairwayOutlines.Count, Is.EqualTo(3), "the shared clip edge breaks the lower half's ring into two open runs and the upper half's into one");
			Assert.That(chart.Coastlines.Count, Is.EqualTo(1), "only the charted COALNE line: land polygon rings are never stroked");
		}

		[Test]
		public void ACellClipEdge_IsNeverStroked()
		{
			var chart = Build();
			// No coastline or fairway outline segment lies along the land split (north 1250) or the fairway split (north -1500).
			AssertNoSegmentAlong(chart.Coastlines, SyntheticHarbor.SplitNorth, "coastline along the land cells' clip edge");
			AssertNoSegmentAlong(chart.FairwayOutlines, -1500.0, "fairway outline along the fairway cells' clip edge");
			// The real coast around the block is still there, including the edge at north 2000.
			Assert.That(SegmentsAlong(chart.Coastlines, 2000.0), Is.EqualTo(1), "the charted coast's north edge");
			// The fairway outlines still have their outer edges.
			Assert.That(SegmentsAlong(chart.FairwayOutlines, -2000.0) + SegmentsAlong(chart.FairwayOutlines, -1000.0), Is.EqualTo(2));
		}

		private static void AssertNoSegmentAlong(ChartTessellation.LineSet set, double north, string what) => Assert.That(SegmentsAlong(set, north), Is.Zero, what);

		private static int SegmentsAlong(ChartTessellation.LineSet set, double north)
		{
			var count = 0;
			for (var item = 0; item < set.Count; item++)
			{
				var first = set.Start[item];
				for (var i = 1; i < set.Length[item]; i++)
				{
					if (System.Math.Abs(set.North[first + i - 1] - north) < 1e-9 && System.Math.Abs(set.North[first + i] - north) < 1e-9) count++;
				}
			}

			return count;
		}

		[Test]
		public void Aids_Classified_AndTheBoundaryAidLeftOut()
		{
			var chart = Build();
			Assert.That(chart.Aids.Count, Is.EqualTo(7), "eight aids less the one outside coverage");
			Assert.That(chart.Aids.Kind[0], Is.EqualTo(AidKind.PortBuoy));
			Assert.That(chart.Aids.Kind[1], Is.EqualTo(AidKind.StarboardBuoy));
			Assert.That(chart.Aids.Kind[2], Is.EqualTo(AidKind.SpecialBuoy));
			Assert.That(chart.Aids.Kind[3], Is.EqualTo(AidKind.PortBeacon));
			Assert.That(chart.Aids.Kind[4], Is.EqualTo(AidKind.Light));
			Assert.That(chart.Aids.Kind[5], Is.EqualTo(AidKind.SpecialBuoy), "BOYSPP without a colour is still a special mark");
			Assert.That(chart.Aids.Kind[6], Is.EqualTo(AidKind.PreferredChannelToStarboard));
			Assert.That(chart.Aids.LightText[0], Is.EqualTo("Fl R 4s"));
			Assert.That(chart.Aids.Lit[2], Is.False);
			Assert.That(chart.Aids.LightText[2], Is.Empty);
			foreach (var north in chart.Aids.North) Assert.That(north, Is.LessThan(2500));
			Assert.That(ChartTessellation.ClassifyAid("DAYMAR", null, null), Is.EqualTo(AidKind.OtherBeacon));
			Assert.That(ChartTessellation.ClassifyAid("BOYLAT", "preferred channel to port", null), Is.EqualTo(AidKind.PreferredChannelToPort));
		}

		[Test]
		public void Landings_Barges_AndBridges_FromTheRuntimePack()
		{
			var chart = Build();
			Assert.That(chart.Stops.Name, Is.EqualTo(new[] { SyntheticHarbor.PierA, SyntheticHarbor.PierB }), "the names the picker shows");
			Assert.That(chart.Barges.Count, Is.EqualTo(1));
			Assert.That(chart.Barges.Length[0], Is.EqualTo(5), "a closed ring");
			Assert.That(chart.Bridges.Count, Is.EqualTo(1));
			Assert.That(chart.Chart[ChartLayer.Soundings].Features.Count, Is.EqualTo(300));
		}
	
		/// <summary>Review item 2: two closed triangles meeting at one vertex share no edge, but their first/last edges collide under the old polynomial key.</summary>
		[Test]
		public void DistinctFairwayEdgesMustNotDisappearOnHashCollision()
		{
			var set = new ChartTessellation.LineSet();
			set.North.AddRange(new[] { 0.0, 100.0, 0.0, 0.0, 101.0, 0.0, 150.0, 101.0 });
			set.East.AddRange(new[] { 0.0, 0.0, 100.0, 0.0, -31.0, 0.0, -60.0, -31.0 });
			set.Start.AddRange(new[] { 0, 4 });
			set.Length.AddRange(new[] { 4, 4 });
			set.MinNorth.AddRange(new[] { 0.0, 0.0 }); set.MinEast.AddRange(new[] { 0.0, -60.0 }); set.MaxNorth.AddRange(new[] { 100.0, 150.0 }); set.MaxEast.AddRange(new[] { 100.0, 0.0 });
			set.Value.AddRange(new[] { double.NaN, double.NaN });
			set.FeatureItemStart = new[] { 0, 1 };
			set.FeatureItemCount = new[] { 1, 1 };
			typeof(ChartTessellation).GetMethod("DropSharedEdges", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, new object[] { set });
			var edges = 0;
			for (var i = 0; i < set.Count; i++) edges += set.Length[i] - 1;
			TestContext.WriteLine("distinct polygon segments before=6 after=" + edges);
			Assert.That(edges, Is.EqualTo(6), "no edge of these two non-overlapping triangles is shared");
		}

		/// <summary>The true reversed seam between two cells is still dropped, and only it.</summary>
		[Test]
		public void ATrueReversedSeamIsStillDropped()
		{
			var set = new ChartTessellation.LineSet();
			// Two squares sharing the edge (0,0)-(0,100): the first traces it north-up, the second south-down.
			set.North.AddRange(new[] { 0.0, 0.0, 100.0, 100.0, 0.0, 0.0, 0.0, -100.0, -100.0, 0.0 });
			set.East.AddRange(new[] { 0.0, 100.0, 100.0, 0.0, 0.0, 100.0, 0.0, 0.0, 100.0, 100.0 });
			set.Start.AddRange(new[] { 0, 5 });
			set.Length.AddRange(new[] { 5, 5 });
			set.MinNorth.AddRange(new[] { 0.0, -100.0 }); set.MinEast.AddRange(new[] { 0.0, 0.0 }); set.MaxNorth.AddRange(new[] { 100.0, 0.0 }); set.MaxEast.AddRange(new[] { 100.0, 100.0 });
			set.Value.AddRange(new[] { double.NaN, double.NaN });
			set.FeatureItemStart = new[] { 0, 1 };
			set.FeatureItemCount = new[] { 1, 1 };
			typeof(ChartTessellation).GetMethod("DropSharedEdges", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, new object[] { set });
			var edges = 0;
			for (var i = 0; i < set.Count; i++) edges += set.Length[i] - 1;
			Assert.That(edges, Is.EqualTo(6), "8 edges less the seam traced twice");
		}
}
}
