using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// The commanded position of one engine lever (<c>ControlInputs</c>), drawn like the control head:
	/// ahead at the top, astern at the bottom, neutral in the middle. It shows the command, not the
	/// clutch: the gear badge says what the clutch is doing. When the vessel file gives the clutch-in
	/// position, small notches mark where the clutch engages.
	/// </summary>
	public sealed class LeverIndicator : InstrumentElement
	{
		private const float Inset = 7f;

		private readonly Label _ahead;
		private readonly Label _neutral;
		private readonly Label _astern;

		public LeverIndicator()
		{
			AddToClassList("fs-lever");
			_ahead = PaletteRoles.Label("AHD", PaletteRoles.Dim, "fs-lever-mark");
			_neutral = PaletteRoles.Label("N", PaletteRoles.Dim, "fs-lever-mark");
			_astern = PaletteRoles.Label("AST", PaletteRoles.Dim, "fs-lever-mark");
			Add(_ahead);
			Add(_neutral);
			Add(_astern);
			RegisterCallback<GeometryChangedEvent>(_ => LayoutMarks());
		}

		/// <summary>Lever command in [-1, 1] last shown; NaN when there is none.</summary>
		public double Lever { get; private set; } = double.NaN;

		/// <summary>Clutch-in lever position from the vessel file; NaN draws no notches.</summary>
		public double ClutchInLever { get; private set; } = double.NaN;

		public void SetLever(double lever)
		{
			if (lever.Equals(Lever))
			{
				return;
			}

			Lever = lever;
			MarkDirtyRepaint();
		}

		public void SetClutchIn(double clutchInLever)
		{
			if (clutchInLever.Equals(ClutchInLever))
			{
				return;
			}

			ClutchInLever = clutchInLever;
			MarkDirtyRepaint();
		}

		protected override void Draw(Painter2D painter, Rect rect)
		{
			var trackX = TrackCentre(rect);
			var top = rect.yMin + Inset;
			var bottom = rect.yMax - Inset;
			var track = new Rect(trackX - 6f, top, 12f, bottom - top);
			VectorDraw.RoundedRect(painter, track, 6f, Palette.Bezel);

			for (var i = -4; i <= 4; i++)
			{
				var y = Y(rect, i / 4.0);
				var length = i == 0 ? 16f : i % 2 == 0 ? 10f : 6f;
				VectorDraw.Line(painter, new Vector2(track.xMax + 3f, y), new Vector2(track.xMax + 3f + length, y), Palette.Scale, i == 0 ? 2f : 1.2f);
			}

			if (InstrumentFormat.IsFinite(ClutchInLever))
			{
				foreach (var detent in new[] { ClutchInLever, -ClutchInLever })
				{
					var y = Y(rect, detent);
					VectorDraw.Line(painter, new Vector2(track.xMin - 3f, y), new Vector2(track.xMax + 3f, y), Palette.TextDim, 1.5f);
				}
			}

			if (!InstrumentFormat.IsFinite(Lever))
			{
				return;
			}

			var lever = Math.Max(-1.0, Math.Min(1.0, Lever));
			var zero = Y(rect, 0.0);
			var handle = Y(rect, lever);
			VectorDraw.Rect(painter, new Rect(track.xMin + 1f, Mathf.Min(zero, handle), track.width - 2f, Mathf.Abs(handle - zero)), Palette.Fill);
			var handleRect = new Rect(trackX - 14f, handle - 5f, 28f, 10f);
			VectorDraw.RoundedRect(painter, new Rect(handleRect.x - 1.5f, handleRect.y - 1.5f, handleRect.width + 3f, handleRect.height + 3f), 4f, Palette.Face);
			VectorDraw.RoundedRect(painter, handleRect, 3f, Palette.Needle);
		}

		private static float TrackCentre(Rect rect) => rect.xMin + rect.width * 0.6f;

		private static float Y(Rect rect, double lever)
		{
			var top = rect.yMin + Inset;
			var bottom = rect.yMax - Inset;
			return (float)((top + bottom) * 0.5 - lever * (bottom - top) * 0.5);
		}

		private void LayoutMarks()
		{
			var rect = contentRect;
			if (rect.height < 1f || float.IsNaN(rect.height))
			{
				return;
			}

			// Left of the handle's widest point, so a lever at full travel never covers its mark.
			var x = rect.xMin + 12f;
			Place(_ahead, x, Y(rect, 1.0) + 6f);
			Place(_neutral, x, Y(rect, 0.0));
			Place(_astern, x, Y(rect, -1.0) - 6f);
		}

		private static void Place(VisualElement label, float x, float y)
		{
			label.style.position = Position.Absolute;
			label.style.left = x;
			label.style.top = y;
			label.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-50f));
		}
	}
}
