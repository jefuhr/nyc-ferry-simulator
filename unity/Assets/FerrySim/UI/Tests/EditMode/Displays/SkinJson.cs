using System.Collections.Generic;

namespace FerrySim.UI.Tests.EditMode.Displays
{
	/// <summary>Builds skin JSON for the tests. Single quotes stand for double quotes so the cases stay readable.</summary>
	internal static class SkinJson
	{
		public static readonly IReadOnlyCollection<string> TClassAlarms = new HashSet<string>
		{
			"navigation.shallow-water",
			"hull.grounding",
			"hull.contact",
			"steering.failure",
		};

		public static string Q(string text) => text.Replace('\'', '"');

		/// <summary>A skin with the given display, pages (a JSON array or null) and element objects.</summary>
		public static string Skin(string elements, string pages = null, string display = "engine", double refreshHz = 10, string reference = "[800, 480]")
		{
			var pageText = pages == null ? "" : "'pages': " + pages + ", ";
			return Q("{'version': 1, 'display': '" + display + "', 'id': 'test-a', 'reference': " + reference + ", 'refreshHz': "
				+ refreshHz.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", " + pageText + "'elements': [" + elements + "]}");
		}

		/// <summary>One element of every kind on two pages: the shape a real engine skin has.</summary>
		public static string EveryElementType() => Skin(
			"{'type': 'frame', 'rect': [0, 0, 800, 40], 'text': '{side} ENGINE'}," +
			"{'type': 'dial', 'rect': [16, 52, 300, 300], 'page': 'main', 'channel': 'engine.{side}.speed', 'range': [0, 2400], 'sweep': [-135, 135], 'unit': 'rpm', 'decimals': 0, 'text': 'RPM'," +
			" 'bands': [{'range': [2100, 2400], 'colour': 'redline'}]}," +
			"{'type': 'bar', 'rect': [16, 364, 300, 44], 'page': 'main', 'channel': 'engine.{side}.load', 'range': [0, 100], 'unit': 'pct', 'decimals': 0, 'text': 'LOAD'}," +
			"{'type': 'value', 'rect': [336, 52, 216, 70], 'page': 'main', 'channel': 'engine.{side}.oil-pressure', 'unit': 'psi', 'decimals': 1, 'text': 'OIL PRESS'}," +
			"{'type': 'value', 'rect': [568, 52, 216, 70], 'page': 'main', 'channel': 'engine.{side}.shift-phase', 'format': 'gear', 'text': 'GEAR'}," +
			"{'type': 'lamp', 'rect': [336, 140, 216, 40], 'page': 'alarms', 'alarm': 'steering.failure', 'text': 'STEERING', 'colour': 'caution'}," +
			"{'type': 'text', 'rect': [568, 140, 216, 30], 'text': 'GENERIC', 'colour': '#FFC53D'}," +
			"{'type': 'list', 'rect': [16, 52, 768, 356], 'page': 'alarms', 'group': 'Engine', 'text': 'NO ENGINE ALARMS'}," +
			"{'type': 'softkey', 'rect': [16, 428, 150, 44], 'text': 'MAIN', 'target': 'main'}," +
			"{'type': 'softkey', 'rect': [182, 428, 150, 44], 'text': 'ALARMS', 'target': 'alarms'}," +
			"{'type': 'softkey', 'rect': [348, 428, 150, 44], 'text': 'ACK', 'action': 'acknowledge'}",
			"['main', 'alarms']");
	}
}
