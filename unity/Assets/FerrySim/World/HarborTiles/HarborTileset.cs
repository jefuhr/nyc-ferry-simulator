using System;
using System.IO;
using CesiumForUnity;
using UnityEngine;

namespace FerrySim.World.HarborTiles
{
	/// <summary>A local-only Cesium 1.26.0 layer, independent of contacts, water depth, and runtime query geometry.</summary>
	[DisallowMultipleComponent]
	[RequireComponent(typeof(Cesium3DTileset))]
	public class HarborTileset : MonoBehaviour, IHarborViewLayer
	{
		[SerializeField] private string layerId = "terrain";
		[SerializeField] private string tilesetFile = "data/processed/harbor/3dtiles/tileset.json";
		[SerializeField] private bool required = true;
		[SerializeField] private long maximumCachedBytes = 192L * 1024 * 1024;
		[SerializeField] private float maximumScreenSpaceError = 16;
		[SerializeField] private Material material;
		private Cesium3DTileset _tileset;
		private bool _opened;
		private bool _subscribed;

		public string LayerId => layerId;
		public bool Required => required;
		public string Failure { get; private set; }
		public bool HasContent { get; private set; }
		public int CreatedTiles { get; private set; }
		public string ResolvedPath { get; private set; }
		public string RelativePath => tilesetFile;
		public Cesium3DTileset Tileset => _tileset != null ? _tileset : _tileset = GetComponent<Cesium3DTileset>();
		public float LoadProgress => _opened && Failure == null && Tileset.enabled ? Tileset.ComputeLoadProgress() : 0f;

		public void Configure(string id, string file, bool isRequired, long cacheBytes, float screenError, Material surfaceMaterial)
		{
			if (_opened) throw new InvalidOperationException("A running harbor layer cannot change its inventory.");
			if (string.IsNullOrWhiteSpace(id) || cacheBytes <= 0 || !(screenError > 0) || float.IsInfinity(screenError))
				throw new ArgumentException("Harbor layers need an ID, positive cache cap and finite screen error.");
			layerId = id;
			tilesetFile = file;
			required = isRequired;
			maximumCachedBytes = cacheBytes;
			maximumScreenSpaceError = screenError;
			material = surfaceMaterial;
			ApplySettings();
		}

		public void ApplySettings()
		{
			var tileset = Tileset;
			tileset.enabled = false;
			tileset.tilesetSource = CesiumDataSource.FromUrl;
			tileset.url = string.Empty;
			tileset.ionAccessToken = string.Empty;
			tileset.ionAssetID = 0;
			tileset.showCreditsOnScreen = false;
			tileset.updateInEditor = false;
			tileset.createPhysicsMeshes = false;
			tileset.maximumCachedBytes = maximumCachedBytes;
			tileset.maximumScreenSpaceError = maximumScreenSpaceError;
			tileset.maximumSimultaneousTileLoads = 20;
			tileset.loadingDescendantLimit = 20;
			tileset.preloadAncestors = true;
			tileset.preloadSiblings = true;
			tileset.forbidHoles = false;
			tileset.enableFrustumCulling = true;
			tileset.enableFogCulling = true;
			tileset.enforceCulledScreenSpaceError = true;
			tileset.culledScreenSpaceError = 64;
			tileset.generateSmoothNormals = false;
			tileset.ignoreKhrMaterialsUnlit = true;
			tileset.opaqueMaterial = material;
		}

		public void Open()
		{
			if (_opened) return;
			try
			{
				var repository = Directory.GetParent(Application.dataPath).Parent.FullName;
				ResolvedPath = HarborTilePaths.Resolve(tilesetFile, Application.isEditor, repository, Application.streamingAssetsPath);
				HarborTileInventory.Verify(ResolvedPath);
				if (material == null || material.shader == null || !material.shader.isSupported)
					throw new InvalidDataException(layerId + ": the production tile material is missing or unsupported");
				ApplySettings();
				Failure = null;
				HasContent = false;
				CreatedTiles = 0;
				Tileset.OnTileGameObjectCreated += OnTileCreated;
				Cesium3DTileset.OnCesium3DTilesetLoadFailure += OnFailure;
				_subscribed = true;
				Tileset.url = HarborTilePaths.Url(ResolvedPath);
				_opened = true;
				Tileset.enabled = true;
			}
			catch (Exception error)
			{
				Failure = (ResolvedPath ?? tilesetFile) + ": " + error.Message;
			}
		}

		private void OnTileCreated(GameObject tile)
		{
			CreatedTiles++;
			HasContent = true;
		}

		private void OnFailure(Cesium3DTilesetLoadFailureDetails details)
		{
			if (details.tileset == _tileset) Failure = ResolvedPath + ": " + details.message;
		}

		private void OnDestroy()
		{
			if (!_subscribed) return;
			if (_tileset != null) _tileset.OnTileGameObjectCreated -= OnTileCreated;
			Cesium3DTileset.OnCesium3DTilesetLoadFailure -= OnFailure;
		}
	}
}
