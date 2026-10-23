using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FerrySim.Cli
{
	/// <summary>Renders a <see cref="MetricsReport"/> for people or as JSON.</summary>
	public static class SummaryFormatter
	{
		public static string Text(MetricsReport report)
		{
			var builder = new StringBuilder();
			builder.Append("Vessel:       ").Append(report.Vessel).Append('\n');
			builder.Append("Maneuver:     ").Append(report.Maneuver);
			if (report.Parameters.Count > 0)
			{
				builder.Append(" (");
				for (var i = 0; i < report.Parameters.Count; i++)
				{
					builder.Append(i > 0 ? ", " : string.Empty).Append(report.Parameters[i].Key).Append('=').Append(report.Parameters[i].Value);
				}

				builder.Append(')');
			}

			builder.Append('\n');
			builder.Append("Environment:  ").Append(report.Environment).Append('\n');
			builder.Append("Model:        ").Append(report.Model).Append('\n');
			builder.Append('\n');

			var width = 0;
			foreach (var metric in report.Metrics)
			{
				width = Math.Max(width, metric.Label.Length);
			}

			foreach (var metric in report.Metrics)
			{
				var value = metric.Value.HasValue
					? metric.Value.Value.ToString("F2", CultureInfo.InvariantCulture) + " " + metric.Unit
					: "not reached";
				builder.Append(metric.Label.PadRight(width + 2)).Append(value).Append('\n');
			}

			if (report.Notes.Count > 0)
			{
				builder.Append('\n');
				foreach (var note in report.Notes)
				{
					builder.Append("Note: ").Append(note).Append('\n');
				}
			}

			return builder.ToString();
		}

		public static string Json(MetricsReport report)
		{
			using (var stream = new MemoryStream())
			{
				using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
				{
					writer.WriteStartObject();
					writer.WriteString("vessel", report.Vessel);
					writer.WriteString("maneuver", report.Maneuver);
					writer.WriteString("environment", report.Environment);
					writer.WriteString("model", report.Model);
					writer.WriteStartObject("parameters");
					foreach (var pair in report.Parameters)
					{
						writer.WriteString(pair.Key, pair.Value);
					}

					writer.WriteEndObject();
					writer.WriteStartObject("metrics");
					foreach (var metric in report.Metrics)
					{
						if (metric.Value.HasValue)
						{
							writer.WriteNumber(metric.Key, Math.Round(metric.Value.Value, 4));
						}
						else
						{
							writer.WriteNull(metric.Key);
						}
					}

					writer.WriteEndObject();
					writer.WriteStartArray("notes");
					foreach (var note in report.Notes)
					{
						writer.WriteStringValue(note);
					}

					writer.WriteEndArray();
					writer.WriteEndObject();
				}

				return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
			}
		}
	}
}
