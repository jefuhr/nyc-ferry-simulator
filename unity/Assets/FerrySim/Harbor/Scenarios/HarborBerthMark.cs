using System;

namespace FerrySim.Harbor
{
	/// <summary>
	/// What the harbor landing audit (data-pipeline/ferrydata/harbor_landings) says about a landing or one of its berths,
	/// handed to <see cref="HarborScenarioCatalog"/> at integration: a note the picker shows beside the name and, when the
	/// audit refuses starts there, the reason <see cref="HarborScenarioCatalog.TryCreate"/> gives. A mark without a berth id
	/// applies to the whole landing, every berth included. The catalog never invents a mark: without any, a berth's note is
	/// the evidence its contact data carries.
	/// </summary>
	public sealed class HarborBerthMark
	{
		/// <param name="stopId">The landing's GTFS stop id.</param>
		/// <param name="berthId">The berth (<c>enc:…</c> or <c>derived-barge:…</c>), or null for the whole landing.</param>
		/// <param name="note">What the picker shows beside the name; null or empty keeps the evidence note.</param>
		/// <param name="refusal">Why a start there is refused, as the rest of a sentence ("the ramp side is unconfirmed by crew"); null when starts stay allowed.</param>
		public HarborBerthMark(string stopId, string berthId, string note, string refusal)
		{
			StopId = stopId ?? throw new ArgumentNullException(nameof(stopId));
			BerthId = string.IsNullOrEmpty(berthId) ? null : berthId;
			Note = note ?? string.Empty;
			Refusal = string.IsNullOrWhiteSpace(refusal) ? null : refusal.Trim().TrimEnd('.');
		}

		public string StopId { get; }

		public string BerthId { get; }

		public string Note { get; }

		public string Refusal { get; }

		/// <summary>True when the mark is about <paramref name="berthId"/> at <paramref name="stopId"/>: its own berth, or any berth of its landing.</summary>
		public bool Applies(string stopId, string berthId) => StopId == stopId && (BerthId == null || BerthId == berthId);
	}
}
