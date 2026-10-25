using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>
	/// The tide's trend beside its word: a triangle up while rising, down while falling, a bar while steady, nothing
	/// without a tide. Drawn rather than typed, like the shaft arrow, so it does not depend on the font having arrow glyphs.
	/// </summary>
	public sealed class TrendArrow : InstrumentElement
	{
		public TrendArrow()
		{
			AddToClassList("fs-trend-arrow");
		}

		public TideTrend Trend { get; private set; } = TideTrend.Unknown;

		public void Show(TideTrend trend)
		{
			if (trend == Trend)
			{
				return;
			}

			Trend = trend;
			MarkDirtyRepaint();
		}

		protected override void Draw(Painter2D painter, Rect rect)
		{
			var centre = rect.center;
			var half = Mathf.Min(rect.width, rect.height) * 0.45f;
			switch (Trend)
			{
				case TideTrend.Rising:
					VectorDraw.Triangle(painter, new Vector2(centre.x, centre.y - half), new Vector2(centre.x - half, centre.y + half * 0.7f),
						new Vector2(centre.x + half, centre.y + half * 0.7f), Palette.Text);
					break;
				case TideTrend.Falling:
					VectorDraw.Triangle(painter, new Vector2(centre.x, centre.y + half), new Vector2(centre.x - half, centre.y - half * 0.7f),
						new Vector2(centre.x + half, centre.y - half * 0.7f), Palette.Text);
					break;
				case TideTrend.Steady:
					VectorDraw.Rect(painter, new Rect(centre.x - half, centre.y - 1.5f, half * 2f, 3f), Palette.TextDim);
					break;
			}
		}
	}
}
