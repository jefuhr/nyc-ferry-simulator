using System;
using CesiumForUnity;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace FerrySim.World.HarborNavigation.Tests
{
	public class RegionalAnchorTests
	{
		[Test] public void CellCornersMatchIndependentAnchorsBeforeAndAfterShifts()
		{
			var root = new GameObject("regional precision fixture");
			try
			{
				var geo = root.AddComponent<CesiumGeoreference>(); geo.SetOriginLongitudeLatitudeHeight(-74.015, 40.7, 0);
				var worst = 0f;
				foreach (var centre in new[] { new Vector2(-7000, -7000), new Vector2(1000, 1000), new Vector2(15000, 15000) })
				{
					var regionHost = new GameObject("region"); regionHost.transform.SetParent(root.transform, false);
					var region = new NavigationAnchor(regionHost, geo, centre.x, centre.y, 0);
					foreach (var north in new[] { -1000.0, 1000.0 }) foreach (var east in new[] { -1000.0, 1000.0 })
					{
						var independent = new GameObject("per-object reference"); independent.transform.SetParent(root.transform, false);
						new NavigationAnchor(independent, geo, centre.x + north, centre.y + east, -31.8);
						var grouped = new GameObject("regional child");
						var child = new NavigationAnchor(grouped, geo, centre.x + north, centre.y + east, -31.8, region);
						Assert.That(grouped.GetComponent<CesiumGlobeAnchor>(), Is.Null);
						Assert.Throws<InvalidOperationException>(() => child.SetHeight(-30));
						var ownOrigin = independent.GetComponent<CesiumGlobeAnchor>().longitudeLatitudeHeight;
						foreach (var origin in new[] { ownOrigin, new double3(-74.015, 40.7, 0), new double3(-73.8, 40.9, 15), new double3(-74.1, 40.6, -5) })
						{
							geo.SetOriginLongitudeLatitudeHeight(origin.x, origin.y, origin.z);
							foreach (var point in new[] { Vector3.zero, new Vector3(4, 1.2f, 13), new Vector3(-4, 40, -13) })
							{
								var error = Vector3.Distance(grouped.transform.TransformPoint(point), independent.transform.TransformPoint(point));
								worst = Mathf.Max(worst, error);
								Assert.That(error, Is.LessThan(origin.Equals(ownOrigin) ? .001 : .01), "cell " + centre + " corner " + north + "," + east + " point " + point);
							}
						}
						UnityEngine.Object.DestroyImmediate(grouped); UnityEngine.Object.DestroyImmediate(independent);
					}
					UnityEngine.Object.DestroyImmediate(regionHost);
				}
				TestContext.WriteLine("144 cell-corner/frame checks; worst alignment_m=" + worst);
			}
			finally { UnityEngine.Object.DestroyImmediate(root); }
		}
	}
}
