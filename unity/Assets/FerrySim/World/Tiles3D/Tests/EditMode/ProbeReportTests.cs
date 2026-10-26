using NUnit.Framework;

namespace FerrySim.World.Tiles3D.Tests
{
	/// <summary>
	/// The probe's verdict (<see cref="ProbeReport.Verdict"/>). Failure modes guarded: the probe passing with zero tiles
	/// loaded; passing with Cesium load failures or errors in the log; passing when the view never finished loading, when
	/// the packaged files are missing or changed, when the screen stayed blank, or when the URL was not a local file.
	/// </summary>
	public sealed class ProbeReportTests
	{
		private static ProbeReport Good() => new ProbeReport
		{
			tileset_url = "file:///C:/Users/Ferry%20Crew/FerrySim%20Tiles3D%20Probe/Tiles3DProbe_Data/StreamingAssets/harbor3d/tileset.json",
			payload_files = 989,
			tiles_loaded = 412,
			first_view_loaded = true,
			final_view_loaded = true,
			wait_timeout_s = 60,
			screenshots = new[] { "probe-start.png", "probe-end.png" },
			screenshot_colours = new[] { 21000, 18000 },
		};

		[Test]
		public void AGoodRunPasses()
		{
			Assert.That(ProbeReport.Verdict(Good()), Is.Empty);
		}

		[Test]
		public void ZeroTilesLoadedFails()
		{
			var report = Good();
			report.tiles_loaded = 0;

			Assert.That(ProbeReport.Verdict(report), Has.Some.Contains("loaded no tile"));
		}

		[Test]
		public void ZeroTilesLoadedFailsEvenWhenCesiumSaysTheViewIsLoaded()
		{
			// Cesium's load progress reads 100% before it has asked for anything; the count is what proves tiles arrived.
			var report = Good();
			report.tiles_loaded = 0;
			report.load_progress_final = 100f;
			report.screenshot_colours = new[] { 1, 1 };

			var failures = ProbeReport.Verdict(report);
			Assert.That(failures, Has.Some.Contains("loaded no tile"));
			Assert.That(failures, Has.Some.Contains("nothing was drawn"));
		}

		[Test]
		public void LoadFailuresAndCesiumErrorsFail()
		{
			var report = Good();
			report.tiles_failed = 3;
			Assert.That(ProbeReport.Verdict(report), Has.Some.Contains("failed tile loads"));

			report = Good();
			report.tileset_load_failures = 1;
			Assert.That(ProbeReport.Verdict(report), Has.Some.Contains("tileset load failures"));

			report = Good();
			report.cesium_errors = new[] { "Error: Received status code 404 for tile content file:///C:/x/c0_0_l0.glb" };
			Assert.That(ProbeReport.Verdict(report), Has.Some.Contains("errors or warnings"));

			report = Good();
			report.errors = 2;
			Assert.That(ProbeReport.Verdict(report), Has.Some.Contains("errors were logged"));
		}

		[Test]
		public void AViewThatNeverLoadsFails()
		{
			var report = Good();
			report.first_view_loaded = false;
			Assert.That(ProbeReport.Verdict(report), Has.Some.Contains("first view"));

			report = Good();
			report.final_view_loaded = false;
			Assert.That(ProbeReport.Verdict(report), Has.Some.Contains("after the flight"));
		}

		[Test]
		public void MissingOrChangedPackagedFilesFail()
		{
			var report = Good();
			report.payload_missing = 1;
			Assert.That(ProbeReport.Verdict(report), Has.Some.Contains("missing"));

			report = Good();
			report.payload_files = 0;
			Assert.That(ProbeReport.Verdict(report), Has.Some.Contains("no packaged tiles"));
		}

		[Test]
		public void ABlankScreenOrNoScreenshotFails()
		{
			var report = Good();
			report.screenshot_colours = new[] { 21000, 3 };
			Assert.That(ProbeReport.Verdict(report), Has.Some.Contains("probe-end.png has 3 colours"));

			report = Good();
			report.screenshots = new string[0];
			report.screenshot_colours = new int[0];
			Assert.That(ProbeReport.Verdict(report), Has.Some.Contains("no screenshot"));
		}

		[Test]
		public void ANonLocalUrlFails()
		{
			var report = Good();
			report.tileset_url = "https://example.com/tileset.json";

			Assert.That(ProbeReport.Verdict(report), Has.Some.Contains("not a local file:// URL"));
		}

		[Test]
		public void PercentilesUseTheNearestRank()
		{
			var sorted = new[] { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0 };

			Assert.That(ProbeReport.Percentile(sorted, 0.5), Is.EqualTo(5.0));
			Assert.That(ProbeReport.Percentile(sorted, 0.95), Is.EqualTo(10.0));
			Assert.That(ProbeReport.Percentile(sorted, 0.0), Is.EqualTo(1.0));
			Assert.That(ProbeReport.Percentile(new double[0], 0.5), Is.EqualTo(0.0));
		}
	}
}
