using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using CesiumForUnity;
using FerrySim.Core.Contracts;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.Sim;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace FerrySim.Harbor.Tests.PlayMode
{
	public class HarborPrecisionTests
	{
		[Serializable] private sealed class Points { public Point[] points; }
		[Serializable] private sealed class Point { public string name; public double north; public double east; }
		private sealed class SpatialWater : IWaterSurface
		{
			public double EllipsoidalHeight(double north, double east, double time) => -31.8 + north * 0.00001 + east * 0.00002 + time * 0.001;
		}

		[UnityTest]
		public IEnumerator AllLandingsAndExtentCorners_CardinalHeadings_StayWithinOneCentimetreAcrossOriginShifts()
		{
			var root = new GameObject("Precision georeference");
			try
			{
				var reference = root.AddComponent<CesiumGeoreference>();
				var vessel = new GameObject("Fake vessel");
				vessel.transform.SetParent(root.transform, false);
				var placement = vessel.AddComponent<CesiumVesselPlacement>();
				var water = new SpatialWater();
				placement.WaterSurface = water;
				var local = new GameObject("Independent local reference mesh");
				local.transform.SetParent(root.transform, false);
				var localAnchor = local.AddComponent<CesiumGlobeAnchor>();
				localAnchor.adjustOrientationForGlobeWhenMoving = false;
				localAnchor.detectTransformChanges = false;
				var fixture = JsonUtility.FromJson<Points>(File.ReadAllText(Path.Combine(Application.dataPath, "FerrySim/Harbor/Tests/Scene/Fixtures/precision-points.json")));
				Assert.That(fixture.points.Length, Is.EqualTo(28));
				var csv = new StringBuilder("point,heading_deg,shifted,near_field_error_m,ecef_error_m\n");
				var worst = 0.0;
				foreach (var point in fixture.points)
				{
					var geo = LocalTangentPlane.World.ToGeodetic(point.north, point.east);
					var height = water.EllipsoidalHeight(point.north, point.east, 37);
					var llh = new double3(geo.LongitudeDegrees, geo.LatitudeDegrees, height);
					var anchorEcef = CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(llh);
					var lat = math.radians(geo.LatitudeDegrees);
					var lon = math.radians(geo.LongitudeDegrees);
					var east = new double3(-Math.Sin(lon), Math.Cos(lon), 0);
					var up = new double3(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
					var north = new double3(-Math.Sin(lat) * Math.Cos(lon), -Math.Sin(lat) * Math.Sin(lon), Math.Cos(lat));
					for (var heading = 0; heading < 360; heading += 90)
					{
						reference.SetOriginLongitudeLatitudeHeight(-74.015, 40.7, 0);
						var angle = math.radians((double)heading);
						var forward = north * Math.Cos(angle) + east * Math.Sin(angle);
						var starboard = east * Math.Cos(angle) - north * Math.Sin(angle);
						// A bow/starboard point 13 m ahead and 4 m across. ECEF subtraction happens in double first.
						var targetEcef = anchorEcef + forward * 13 + starboard * 4 + up * 1.2;
						var delta = targetEcef - anchorEcef;
						var localVertex = new Vector3((float)math.dot(delta, east), (float)math.dot(delta, up), (float)math.dot(delta, north));
						localAnchor.longitudeLatitudeHeight = llh;
						localAnchor.rotationEastUpNorth = quaternion.identity;
						var pose = new VesselPose(point.north, point.east, 0, angle, 0, 0);
						placement.Place(vessel.transform, pose, 37);
						for (var shifted = 0; shifted < 2; shifted++)
						{
							if (shifted == 1)
							{
								// Same georeference relocation used by CesiumOriginShift, near the vessel's camera.
								reference.SetOriginLongitudeLatitudeHeight(geo.LongitudeDegrees, geo.LatitudeDegrees, height + 6);
								var before = vessel.transform.position;
								placement.Place(vessel.transform, pose, 37);
								Assert.That(Vector3.Distance(before, vessel.transform.position), Is.LessThan(0.01));
							}
							var measured = vessel.transform.TransformPoint(new Vector3(4, 1.2f, 13));
							var expected = local.transform.TransformPoint(localVertex);
							var error = Vector3.Distance(measured, expected);
							var ecefError = math.length(placement.Anchor.positionGlobeFixed - anchorEcef);
							worst = Math.Max(worst, error);
							csv.Append('"').Append(point.name).Append("\",").Append(heading).Append(',').Append(shifted).Append(',')
								.Append(error.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(ecefError.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
							Assert.That(error, Is.LessThan(0.01), point.name + " heading " + heading + " shift " + shifted);
							Assert.That(ecefError, Is.LessThan(0.001));
						}
					}
				}
				var folder = Path.Combine(HarborTestRig.Repo, "artifacts/harbor-scene");
				Directory.CreateDirectory(folder);
				File.WriteAllText(Path.Combine(folder, "precision.csv"), csv.ToString());
				TestContext.WriteLine("224 point/heading/origin checks; worst near-field alignment " + worst.ToString("R", CultureInfo.InvariantCulture) + " m");
				yield return null;
			}
			finally { UnityEngine.Object.Destroy(root); }
			yield return null;
		}
	}
}
