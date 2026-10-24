using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace FerrySim.World.Import
{
	/// <summary>Chart rhythm and period, with SIGSEQ durations preferred over nominal pulse widths.</summary>
	[Serializable]
	public sealed class LightCharacteristic
	{
		public string Rhythm;
		public string Colour;
		public double PeriodSeconds;
		public int[] Groups;
		public double[] Durations;
		public bool[] States;
		public bool UsesChartSequence;

		public static bool TryParse(string notation, out LightCharacteristic result)
		{
			result = null;
			var match = Regex.Match(notation ?? "", @"^(Fl|LFl|Q|Iso|Oc|F)(?:\((\d+(?:\+\d+)*)\))?\s+(W|R|G|Y|Bu|Am)(?:\s+(\d+(?:\.\d+)?)s)?$");
			if (!match.Success) return false;
			var rhythm = match.Groups[1].Value;
			var period = rhythm == "F" ? 1.0 : 0.0;
			if (match.Groups[4].Success && !double.TryParse(match.Groups[4].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out period)) return false;
			if (period <= 0 || double.IsInfinity(period)) return false;
			var groupTokens = match.Groups[2].Success ? match.Groups[2].Value.Split('+') : new[] { "1" };
			var groups = new int[groupTokens.Length];
			for (var i = 0; i < groups.Length; i++)
				if (!int.TryParse(groupTokens[i], out groups[i]) || groups[i] <= 0 || groups[i] > 100) return false;
			result = new LightCharacteristic { Rhythm = rhythm, Colour = match.Groups[3].Value, PeriodSeconds = period, Groups = groups };
			return true;
		}

		public void SetSequence(double[] durations, bool[] states)
		{
			if (!ValidSequence(durations, states))
				throw new ArgumentException("SIGSEQ must contain positive finite durations summing to SIGPER.");
			Durations = (double[])durations.Clone(); States = (bool[])states.Clone(); UsesChartSequence = true;
		}

		private bool ValidSequence(double[] durations, bool[] states)
		{
			if (durations == null || states == null || durations.Length == 0 || durations.Length != states.Length) return false;
			var sum = 0.0;
			foreach (var duration in durations)
			{
				if (double.IsNaN(duration) || double.IsInfinity(duration) || duration <= 0) return false;
				sum += duration;
			}
			return !double.IsInfinity(sum) && Math.Abs(sum - PeriodSeconds) <= .01;
		}

		public bool TryValidateFallback(out string warning)
		{
			warning = null;
			if (Groups == null || Groups.Length == 0 || Groups.Any(g => g <= 0 || g > 100)) warning = "invalid nominal group counts";
			else if (Rhythm == "F" && (Groups.Length != 1 || Groups[0] != 1)) warning = "grouped fixed lights require chart SIGSEQ";
			else if (Rhythm == "LFl" && PeriodSeconds <= Groups.Sum(g => (double)g) * 2) warning = "period cannot fit all 2-second long flashes plus eclipses; chart SIGSEQ required";
			return warning == null;
		}

		public bool IsLit(double simulationSeconds)
		{
			if (PeriodSeconds <= 0 || double.IsNaN(PeriodSeconds) || double.IsInfinity(PeriodSeconds) || double.IsNaN(simulationSeconds) || double.IsInfinity(simulationSeconds)) return false;
			var phase = ((simulationSeconds % PeriodSeconds) + PeriodSeconds) % PeriodSeconds;
			if (UsesChartSequence && ValidSequence(Durations, States))
			{
				for (var i = 0; i < Durations.Length; i++) { if (phase < Durations[i]) return States[i]; phase -= Durations[i]; }
				return States[States.Length - 1]; // SIGSEQ sums are accepted within chart rounding tolerance.
			}
			if (!TryValidateFallback(out _)) return false;
			if (Rhythm == "F") return true;
			if (Rhythm == "Q" && Groups.Length == 1 && Groups[0] == 1) return phase % Math.Min(1, PeriodSeconds) < .3 * Math.Min(1, PeriodSeconds);
			// Explicit greybox defaults: compress short flashes to fit, retain every group and a final gap.
			var count = Groups.Sum(g => (double)g); var slots = count + Groups.Length - 1;
			var spacing = Math.Min(1, PeriodSeconds / (slots + 1)); var pulse = .3 * spacing;
			if (Rhythm == "LFl") { pulse = 2; spacing = pulse + Math.Min(1, (PeriodSeconds - count * pulse) / (count + Groups.Length)); }
			if (Rhythm == "Oc" || Rhythm == "Iso") { spacing = PeriodSeconds / slots; pulse = spacing * (Rhythm == "Oc" ? .25 : .5); }
			var cursor = 0.0;
			foreach (var group in Groups)
			{
				for (var i = 0; i < group; i++)
				{
					var start = cursor + (Rhythm == "Oc" ? spacing - pulse : 0);
					if (phase >= start && phase < start + pulse) return Rhythm != "Oc";
					cursor += spacing;
				}
				cursor += Rhythm == "LFl" ? spacing - pulse : spacing;
			}
			return Rhythm == "Oc";
		}
	}
}
