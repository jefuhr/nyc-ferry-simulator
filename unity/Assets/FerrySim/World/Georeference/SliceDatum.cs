using System;
using UnityEngine;

namespace FerrySim.World
{
	/// <summary>
	/// Vertical datum facts for one slice, written at edit time from the data pipeline's outputs by the
	/// world builder (FerrySim > World > Rebuild Greybox From Pipeline Data). Heights are metres.
	/// <list type="bullet">
	/// <item>Geoid: the WGS84 ellipsoidal height of NAVD88 zero (GEOID18) at the slice's bounding-box
	/// corners, from geoid_offset.json. Interpolated bilinearly in latitude and longitude.</item>
	/// <item>Tidal datums at the datum station, as NAVD88 heights, from NOAA CO-OPS station datums. Bridge
	/// clearances are published above MHW, not NAVD88.</item>
	/// </list>
	/// </summary>
	[CreateAssetMenu(menuName = "FerrySim/World/Slice Datum", fileName = "SliceDatum")]
	public sealed class SliceDatum : ScriptableObject
	{
		[SerializeField] private string sliceName = "";

		[Header("Bounding box, WGS84 degrees")]
		[SerializeField] private double southLatitude;
		[SerializeField] private double northLatitude;
		[SerializeField] private double westLongitude;
		[SerializeField] private double eastLongitude;

		[Header("Ellipsoidal height of NAVD88 zero, metres")]
		[SerializeField] private string geoidModel = "";
		[SerializeField] private double geoidSouthWest;
		[SerializeField] private double geoidSouthEast;
		[SerializeField] private double geoidNorthWest;
		[SerializeField] private double geoidNorthEast;
		[Tooltip("The pipeline's own sample at the slice centre, kept to check the interpolation.")]
		[SerializeField] private double geoidCentre;

		[Header("Tidal datums at the datum station, metres above NAVD88")]
		[SerializeField] private string datumStation = "";
		[SerializeField] private string tidalDatumEpoch = "";
		[SerializeField] private double meanHighWaterNavd88;
		[SerializeField] private double meanLowerLowWaterNavd88;

		[TextArea(3, 12)]
		[SerializeField] private string provenance = "";

		public string SliceName => sliceName;

		public double SouthLatitude => southLatitude;

		public double NorthLatitude => northLatitude;

		public double WestLongitude => westLongitude;

		public double EastLongitude => eastLongitude;

		public double CentreLatitude => 0.5 * (southLatitude + northLatitude);

		public double CentreLongitude => 0.5 * (westLongitude + eastLongitude);

		public string GeoidModel => geoidModel;

		/// <summary>The pipeline's geoid sample at the bounding-box centre.</summary>
		public double GeoidCentre => geoidCentre;

		/// <summary>Datum station id and name, for example "8518750 The Battery".</summary>
		public string DatumStation => datumStation;

		public string TidalDatumEpoch => tidalDatumEpoch;

		/// <summary>Mean high water at the datum station, metres above NAVD88. Bridge clearances start here.</summary>
		public double MeanHighWaterNavd88 => meanHighWaterNavd88;

		/// <summary>Mean lower low water (chart datum) at the datum station, metres above NAVD88.</summary>
		public double MeanLowerLowWaterNavd88 => meanLowerLowWaterNavd88;

		public string Provenance => provenance;

		/// <summary>True once the builder has filled the asset; a fresh asset has no geoid.</summary>
		public bool IsPopulated => northLatitude > southLatitude && eastLongitude > westLongitude && geoidModel.Length > 0;

		/// <summary>
		/// Ellipsoidal height of NAVD88 zero (metres above WGS84) at a latitude and longitude, interpolated
		/// bilinearly between the corner samples. Outside the box the nearest edge value is held.
		/// </summary>
		public double GeoidHeight(double latitudeDegrees, double longitudeDegrees)
		{
			if (!IsPopulated)
			{
				throw new InvalidOperationException($"Slice datum '{name}' has no geoid samples; rebuild it from the pipeline outputs.");
			}

			var u = Clamp01((longitudeDegrees - westLongitude) / (eastLongitude - westLongitude));
			var v = Clamp01((latitudeDegrees - southLatitude) / (northLatitude - southLatitude));
			var south = geoidSouthWest + u * (geoidSouthEast - geoidSouthWest);
			var north = geoidNorthWest + u * (geoidNorthEast - geoidNorthWest);
			return south + v * (north - south);
		}

		/// <summary>Fills the asset. Only the edit-time builder and tests call this.</summary>
		public void Assign(SliceDatumValues values)
		{
			sliceName = values.SliceName ?? "";
			southLatitude = values.SouthLatitude;
			northLatitude = values.NorthLatitude;
			westLongitude = values.WestLongitude;
			eastLongitude = values.EastLongitude;
			geoidModel = values.GeoidModel ?? "";
			geoidSouthWest = values.GeoidSouthWest;
			geoidSouthEast = values.GeoidSouthEast;
			geoidNorthWest = values.GeoidNorthWest;
			geoidNorthEast = values.GeoidNorthEast;
			geoidCentre = values.GeoidCentre;
			datumStation = values.DatumStation ?? "";
			tidalDatumEpoch = values.TidalDatumEpoch ?? "";
			meanHighWaterNavd88 = values.MeanHighWaterNavd88;
			meanLowerLowWaterNavd88 = values.MeanLowerLowWaterNavd88;
			provenance = values.Provenance ?? "";
		}

		private static double Clamp01(double value) => value < 0.0 ? 0.0 : value > 1.0 ? 1.0 : value;
	}

	/// <summary>Plain values for <see cref="SliceDatum.Assign"/>.</summary>
	public sealed class SliceDatumValues
	{
		public string SliceName;
		public double SouthLatitude;
		public double NorthLatitude;
		public double WestLongitude;
		public double EastLongitude;
		public string GeoidModel;
		public double GeoidSouthWest;
		public double GeoidSouthEast;
		public double GeoidNorthWest;
		public double GeoidNorthEast;
		public double GeoidCentre;
		public string DatumStation;
		public string TidalDatumEpoch;
		public double MeanHighWaterNavd88;
		public double MeanLowerLowWaterNavd88;
		public string Provenance;
	}
}
