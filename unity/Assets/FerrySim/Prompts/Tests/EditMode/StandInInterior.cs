using System;
using System.Collections.Generic;
using System.Text;
using FerrySim.Vessels;
using FerrySim.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FerrySim.Prompts.Tests.EditMode
{
	/// <summary>
	/// A wheelhouse interior made of plain transforms with a <see cref="WheelhouseControlMap"/>, a control map
	/// parsed through the real <see cref="ControlMapFile.Parse"/>, and a camera at the origin looking along +z.
	/// </summary>
	internal sealed class StandInInterior : IDisposable
	{
		private readonly List<(string mount, string[] actions, string label)> _rows = new List<(string, string[], string)>();

		public StandInInterior()
		{
			Root = new GameObject("WheelhouseInterior (stand-in)");
			Mounts = Root.AddComponent<WheelhouseControlMap>();
			var eye = new GameObject("test camera");
			Camera = eye.AddComponent<Camera>();
			Camera.fieldOfView = 65f;
			Camera.aspect = 16f / 9f;
			Camera.nearClipPlane = 0.05f;
			Camera.farClipPlane = 100f;
		}

		public GameObject Root { get; }

		public WheelhouseControlMap Mounts { get; }

		public Camera Camera { get; }

		/// <summary>Adds a mount at a world position (the root sits at the origin) facing <paramref name="normal"/> (default: towards the camera).</summary>
		public Transform Add(string mount, Vector3 position, string label, params string[] actions) => Add(mount, position, -Vector3.forward, label, actions);

		public Transform Add(string mount, Vector3 position, Vector3 normal, string label, params string[] actions)
		{
			var transform = new GameObject(mount).transform;
			transform.SetParent(Root.transform, false);
			transform.localPosition = position;
			Mounts.Controls.Add(new WheelhouseControlMap.ControlMount
			{
				Id = mount, Kind = "button", Mount = transform, Axis = Vector3.up, Travel = Vector2.zero, Normal = normal, LampIndex = -1,
			});
			_rows.Add((mount, actions, label));
			return transform;
		}

		public ControlMapFile Map()
		{
			var json = new StringBuilder("{\"version\":1,\"controls\":[");
			for (var i = 0; i < _rows.Count; i++)
			{
				var (mount, actions, label) = _rows[i];
				if (i > 0)
				{
					json.Append(',');
				}

				json.Append("{\"mount\":\"").Append(mount).Append('"');
				if (actions.Length > 0)
				{
					json.Append(",\"actions\":[\"").Append(string.Join("\",\"", actions)).Append("\"]");
				}

				if (label != null)
				{
					json.Append(",\"label\":\"").Append(label).Append('"');
				}

				json.Append('}');
			}

			return ControlMapFile.Parse(json.Append("]}").ToString(), Mounts);
		}

		public void Dispose()
		{
			Object.DestroyImmediate(Camera.gameObject);
			if (Root != null)
			{
				Object.DestroyImmediate(Root);
			}
		}
	}
}
