using UnityEngine;

namespace FerrySim.Sim
{
	/// <summary>
	/// Dev-scene water: a large plane with a procedural grid so motion is visible, kept under the vessel by
	/// moving in whole grid cells (the grid never appears to slide). Only for greybox scenes; the real water
	/// belongs to the world.
	/// </summary>
	[RequireComponent(typeof(MeshRenderer))]
	public sealed class DevWater : MonoBehaviour
	{
		private const int TexturePixels = 128;
		private const int LinePixels = 3;

		[SerializeField]
		private Transform follow;

		[Tooltip("Grid cell size, metres.")]
		[SerializeField]
		private float cellSize = 50f;

		[SerializeField]
		private Color waterColor = new Color(0.11f, 0.29f, 0.40f);

		[SerializeField]
		private Color lineColor = new Color(0.27f, 0.50f, 0.63f);

		private Texture2D _grid;

		public Transform Follow
		{
			get => follow;
			set => follow = value;
		}

		private void Start()
		{
			_grid = new Texture2D(TexturePixels, TexturePixels, TextureFormat.RGBA32, true)
			{
				name = "DevWaterGrid",
				wrapMode = TextureWrapMode.Repeat,
				filterMode = FilterMode.Trilinear,
				anisoLevel = 8,
			};
			var pixels = new Color32[TexturePixels * TexturePixels];
			for (var y = 0; y < TexturePixels; y++)
			{
				for (var x = 0; x < TexturePixels; x++)
				{
					var onLine = x < LinePixels || y < LinePixels;
					pixels[y * TexturePixels + x] = onLine ? lineColor : waterColor;
				}
			}

			_grid.SetPixels32(pixels);
			_grid.Apply(true, true);
			var material = GetComponent<MeshRenderer>().material;
			material.mainTexture = _grid;
			// Unity's plane primitive is 10 units across before scaling.
			var size = 10f * transform.lossyScale.x;
			material.mainTextureScale = new Vector2(size / cellSize, size / cellSize);
			if (material.HasProperty("_BaseColor"))
			{
				material.SetColor("_BaseColor", Color.white);
			}
		}

		private void LateUpdate()
		{
			if (follow == null)
			{
				return;
			}

			var target = follow.position;
			var position = transform.position;
			position.x = Mathf.Round(target.x / cellSize) * cellSize;
			position.z = Mathf.Round(target.z / cellSize) * cellSize;
			transform.position = position;
		}

		private void OnDestroy()
		{
			if (_grid != null)
			{
				Destroy(_grid);
			}
		}
	}
}
