using System;
using FerrySim.Core.Contracts.Geodesy;
using FerrySim.World.HarborTiles;
using UnityEngine;

namespace FerrySim.Harbor
{
	[Serializable]
	public sealed class HarborSceneConfiguration
	{
		public string schema;
		public double originLatitude;
		public double originLongitude;
		public double originShiftMetres;
		public float cameraFieldOfView;
		public float wheelhouseFieldOfView;
		public float cameraFarClipMetres;
		public int initialViewStableFrames;
		public double initialViewTimeoutSeconds;
		public string runtimePackFolder;
		public string contactsFile;
		public string environmentFolder;
		public Layer terrain;
		public Layer skyline;

		[Serializable]
		public sealed class Layer
		{
			public string id;
			public string tilesetFile;
			public bool required;
			public long maximumCachedBytes;
			public float maximumScreenSpaceError;
		}

		public static HarborSceneConfiguration Parse(string json)
		{
			var settings = JsonUtility.FromJson<HarborSceneConfiguration>(json);
			if (settings == null || settings.schema != "ferrysim-harbor-scene/1" || settings.originLatitude != WorldOrigin.LatitudeDegrees ||
				settings.originLongitude != WorldOrigin.LongitudeDegrees || settings.originShiftMetres != 100 ||
				!(settings.cameraFieldOfView >= 20 && settings.cameraFieldOfView <= 110) ||
				!(settings.wheelhouseFieldOfView >= 20 && settings.wheelhouseFieldOfView <= 110) ||
				!(settings.cameraFarClipMetres >= 1000 && settings.cameraFarClipMetres <= 100000) ||
				settings.initialViewStableFrames < 1 || !(settings.initialViewTimeoutSeconds > 0 && settings.initialViewTimeoutSeconds <= 300))
				throw new ArgumentException("Invalid harbor-scene.json configuration or shared world origin.");
			Validate(settings.terrain, "terrain");
			Validate(settings.skyline, "skyline");
			HarborTilePaths.Resolve(settings.runtimePackFolder, true, "/", "/");
			HarborTilePaths.Resolve(settings.contactsFile, true, "/", "/");
			HarborTilePaths.Resolve(settings.environmentFolder, true, "/", "/");
			return settings;
		}

		private static void Validate(Layer layer, string id)
		{
			if (layer == null || layer.id != id || !layer.required || layer.maximumCachedBytes <= 0 ||
				!(layer.maximumScreenSpaceError > 0 && layer.maximumScreenSpaceError <= 128))
				throw new ArgumentException("Invalid required harbor render layer: " + id);
			HarborTilePaths.Resolve(layer.tilesetFile, true, "/", "/");
		}
	}
}
