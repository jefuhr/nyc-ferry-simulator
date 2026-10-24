using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// True-heading compass repeater: a card that turns under a fixed lubber line, with its numerals
	/// facing outward like a gyro repeater, and a dashed course-over-ground vector from own ship (in the
	/// COG readout's color) so the difference between heading and COG (set and leeway) is visible at a
	/// glance. The vector is hidden when the boat is too slow for a course to mean anything.
	/// </summary>
	public sealed class CompassRose : VisualElement, IPaletteAware
	{
		private static readonly string[] CardLabels = { "N", "3", "6", "E", "12", "15", "S", "21", "24", "W", "30", "33" };

		private readonly Card _card;
		private readonly Overlay _overlay;
		private readonly Label[] _labels = new Label[CardLabels.Length];

		public CompassRose()
		{
			AddToClassList("fs-compass");
			_card = new Card();
			Add(_card);
			for (var i = 0; i < CardLabels.Length; i++)
			{
				var cardinal = i % 3 == 0;
				_labels[i] = PaletteRoles.Label(CardLabels[i], i == 0 ? PaletteRoles.Text : PaletteRoles.Scale,
					cardinal ? "fs-compass-cardinal" : "fs-compass-numeral");
				_card.Add(_labels[i]);
			}

			_overlay = new Overlay();
			Add(_overlay);
			_card.RegisterCallback<GeometryChangedEvent>(_ => LayoutLabels());
		}

		/// <summary>Heading shown, degrees in [0, 360); NaN when unknown.</summary>
		public double HeadingDegrees { get; private set; } = double.NaN;

		/// <summary>Course over ground shown by the COG vector, degrees; NaN when hidden.</summary>
		public double CourseDegrees => _overlay.CourseDegrees;

		/// <summary>Rotation applied to the card, degrees (minus the heading).</summary>
		public float CardRotationDegrees { get; private set; }

		public void ApplyPalette(InstrumentPalette palette)
		{
			_card.ApplyPalette(palette);
			_overlay.ApplyPalette(palette);
		}

		/// <param name="headingRadians">True heading.</param>
		/// <param name="courseRadians">Course over ground, or NaN to hide the COG vector.</param>
		public void SetHeading(double headingRadians, double courseRadians)
		{
			if (!InstrumentFormat.IsFinite(headingRadians))
			{
				HeadingDegrees = double.NaN;
				_card.style.visibility = Visibility.Hidden;
				_overlay.SetCourse(double.NaN, double.NaN);
				return;
			}

			_card.style.visibility = Visibility.Visible;
			HeadingDegrees = DisplayUnits.CompassDegrees(headingRadians);
			var rotation = (float)-HeadingDegrees;
			if (Math.Abs(rotation - CardRotationDegrees) > 0.05f)
			{
				CardRotationDegrees = rotation;
				_card.style.rotate = new Rotate(new Angle(rotation, AngleUnit.Degree));
			}

			_overlay.SetCourse(
				InstrumentFormat.IsFinite(courseRadians) ? DisplayUnits.CompassDegrees(courseRadians) : double.NaN,
				HeadingDegrees);
		}

		private void LayoutLabels()
		{
			var rect = _card.contentRect;
			if (rect.width < 1f || float.IsNaN(rect.width))
			{
				return;
			}

			var centre = rect.center;
			var radius = Mathf.Min(rect.width, rect.height) * 0.5f;
			for (var i = 0; i < _labels.Length; i++)
			{
				var bearing = i * 30f;
				var point = VectorDraw.Polar(centre, radius * 0.66f, bearing);
				var label = _labels[i];
				label.style.position = Position.Absolute;
				label.style.left = point.x;
				label.style.top = point.y;
				label.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-50f));
				label.style.rotate = new Rotate(new Angle(bearing, AngleUnit.Degree));
			}
		}

		private sealed class Card : InstrumentElement
		{
			public Card()
			{
				AddToClassList("fs-layer");
			}

			protected override void Draw(Painter2D painter, Rect rect)
			{
				var centre = rect.center;
				var radius = Mathf.Min(rect.width, rect.height) * 0.5f - 1f;
				VectorDraw.Circle(painter, centre, radius, Palette.Bezel);
				VectorDraw.Circle(painter, centre, radius - 2f, Palette.Face);
				for (var bearing = 0; bearing < 360; bearing += 5)
				{
					var length = bearing % 30 == 0 ? 0.16f : bearing % 10 == 0 ? 0.10f : 0.06f;
					var width = bearing % 30 == 0 ? 2.2f : 1.1f;
					VectorDraw.Line(painter, VectorDraw.Polar(centre, radius * (0.93f - length), bearing),
						VectorDraw.Polar(centre, radius * 0.93f, bearing), bearing == 0 ? Palette.Text : Palette.Scale, width);
				}
			}
		}

		private sealed class Overlay : InstrumentElement
		{
			private double _heading = double.NaN;

			public Overlay()
			{
				AddToClassList("fs-layer");
			}

			public double CourseDegrees { get; private set; } = double.NaN;

			public void SetCourse(double courseDegrees, double headingDegrees)
			{
				if (courseDegrees.Equals(CourseDegrees) && headingDegrees.Equals(_heading))
				{
					return;
				}

				CourseDegrees = courseDegrees;
				_heading = headingDegrees;
				MarkDirtyRepaint();
			}

			protected override void Draw(Painter2D painter, Rect rect)
			{
				var centre = rect.center;
				var radius = Mathf.Min(rect.width, rect.height) * 0.5f;

				// Lubber line: fixed at the top, pointing into the card.
				VectorDraw.Triangle(painter,
					new Vector2(centre.x, rect.yMin + radius * 0.20f),
					new Vector2(centre.x - radius * 0.07f, rect.yMin + 1f),
					new Vector2(centre.x + radius * 0.07f, rect.yMin + 1f),
					Palette.Needle);

				// Own ship, bow up.
				var bow = new Vector2(centre.x, centre.y - radius * 0.20f);
				var stern = centre.y + radius * 0.16f;
				var beam = radius * 0.07f;
				painter.strokeColor = Palette.Needle;
				painter.lineWidth = 1.6f;
				painter.lineJoin = LineJoin.Round;
				painter.BeginPath();
				painter.MoveTo(bow);
				painter.LineTo(new Vector2(centre.x + beam, centre.y - radius * 0.04f));
				painter.LineTo(new Vector2(centre.x + beam, stern));
				painter.LineTo(new Vector2(centre.x - beam, stern));
				painter.LineTo(new Vector2(centre.x - beam, centre.y - radius * 0.04f));
				painter.ClosePath();
				painter.Stroke();

				if (!InstrumentFormat.IsFinite(CourseDegrees) || !InstrumentFormat.IsFinite(_heading))
				{
					return;
				}

				var relative = (float)(CourseDegrees - _heading);
				VectorDraw.DashedLine(painter, VectorDraw.Polar(centre, radius * 0.24f, relative), VectorDraw.Polar(centre, radius * 0.78f, relative),
					Palette.SeriesB, 2f, 5f, 3f);
				var marker = VectorDraw.Polar(centre, radius * 0.78f, relative);
				var size = radius * 0.07f;
				var outward = VectorDraw.Polar(Vector2.zero, size, relative);
				var across = VectorDraw.Polar(Vector2.zero, size * 0.75f, relative + 90f);
				painter.strokeColor = Palette.SeriesB;
				painter.lineWidth = 2f;
				painter.lineJoin = LineJoin.Miter;
				painter.BeginPath();
				painter.MoveTo(marker + outward);
				painter.LineTo(marker + across);
				painter.LineTo(marker - outward);
				painter.LineTo(marker - across);
				painter.ClosePath();
				painter.Stroke();
			}
		}
	}
}
