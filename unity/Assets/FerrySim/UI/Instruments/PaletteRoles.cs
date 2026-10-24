using UnityEngine;
using UnityEngine.UIElements;

namespace FerrySim.UI
{
	/// <summary>Elements that draw with the palette and must repaint when it changes.</summary>
	public interface IPaletteAware
	{
		void ApplyPalette(InstrumentPalette palette);
	}

	/// <summary>
	/// The style sheet declares which palette role an element plays through these classes; the palette
	/// itself stays in C# (<see cref="InstrumentPalette"/>) so its contrast can be tested. Applying a
	/// palette walks the tree once, colors elements by class and calls <see cref="IPaletteAware"/>.
	/// </summary>
	public static class PaletteRoles
	{
		public const string Text = "fs-c-text";
		public const string Dim = "fs-c-dim";
		public const string Scale = "fs-c-scale";
		public const string Port = "fs-c-port";
		public const string Starboard = "fs-c-stbd";

		/// <summary>Course over ground: the COG caption shares the compass's COG vector and the debrief's COG line color.</summary>
		public const string Course = "fs-c-course";
		public const string ConsoleBackground = "fs-bg-console";
		public const string FaceBackground = "fs-bg-face";
		public const string HudBackground = "fs-bg-hud";

		public static void Apply(VisualElement root, InstrumentPalette palette)
		{
			if (root == null || palette == null)
			{
				return;
			}

			root.Query<VisualElement>().ForEach(element => ApplyTo(element, palette));
		}

		private static void ApplyTo(VisualElement element, InstrumentPalette palette)
		{
			if (element.ClassListContains(Text))
			{
				element.style.color = palette.Text;
			}
			else if (element.ClassListContains(Dim))
			{
				element.style.color = palette.TextDim;
			}
			else if (element.ClassListContains(Scale))
			{
				element.style.color = palette.Scale;
			}
			else if (element.ClassListContains(Port))
			{
				element.style.color = palette.Port;
			}
			else if (element.ClassListContains(Starboard))
			{
				element.style.color = palette.Starboard;
			}
			else if (element.ClassListContains(Course))
			{
				element.style.color = palette.SeriesB;
			}

			if (element.ClassListContains(ConsoleBackground))
			{
				element.style.backgroundColor = palette.Console;
				SetBorderColor(element, palette.Bezel);
			}
			else if (element.ClassListContains(FaceBackground))
			{
				element.style.backgroundColor = palette.Face;
				SetBorderColor(element, palette.Bezel);
			}
			else if (element.ClassListContains(HudBackground))
			{
				element.style.backgroundColor = palette.HudBacking;
				SetBorderColor(element, palette.Bezel);
			}

			if (element is IPaletteAware aware)
			{
				aware.ApplyPalette(palette);
			}
		}

		public static void SetBorderColor(VisualElement element, Color color)
		{
			element.style.borderTopColor = color;
			element.style.borderRightColor = color;
			element.style.borderBottomColor = color;
			element.style.borderLeftColor = color;
		}

		/// <summary>A label carrying a palette role and layout classes.</summary>
		public static Label Label(string text, string role, params string[] classes)
		{
			var label = new Label(text) { pickingMode = PickingMode.Ignore };
			label.AddToClassList("fs-label");
			if (!string.IsNullOrEmpty(role))
			{
				label.AddToClassList(role);
			}

			foreach (var name in classes)
			{
				label.AddToClassList(name);
			}

			return label;
		}

		/// <summary>Sets a label's text only when it changed, so unchanged readouts do not trigger a relayout.</summary>
		public static void SetText(Label label, string text)
		{
			if (label != null && label.text != text)
			{
				label.text = text;
			}
		}
	}

	/// <summary>
	/// Base for vector-drawn instruments: holds the palette, repaints when it changes and calls
	/// <see cref="Draw"/> with the content rectangle whenever UI Toolkit regenerates the element.
	/// </summary>
	public abstract class InstrumentElement : VisualElement, IPaletteAware
	{
		protected InstrumentElement()
		{
			pickingMode = PickingMode.Ignore;
			generateVisualContent += OnGenerateVisualContent;
		}

		protected InstrumentPalette Palette { get; private set; } = InstrumentPalette.Day;

		public virtual void ApplyPalette(InstrumentPalette palette)
		{
			Palette = palette ?? InstrumentPalette.Day;
			MarkDirtyRepaint();
		}

		protected abstract void Draw(Painter2D painter, Rect rect);

		private void OnGenerateVisualContent(MeshGenerationContext context)
		{
			var rect = contentRect;
			if (rect.width < 1f || rect.height < 1f || float.IsNaN(rect.width) || float.IsNaN(rect.height))
			{
				return;
			}

			Draw(context.painter2D, rect);
		}
	}
}
