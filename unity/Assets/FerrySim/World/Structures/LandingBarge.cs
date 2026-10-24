using System;
using UnityEngine;

namespace FerrySim.World
{
	/// <summary>
	/// A greybox landing barge generated from the pipeline's landing_faces.geojson: identity, provenance
	/// and the berthing face in world (north, east) metres, so other systems (contact geometry, UI,
	/// debriefs) can read the face without parsing GeoJSON. Nothing here is surveyed: every face needs
	/// crew verification, and the confidence says how much the outline itself can be trusted.
	/// </summary>
	[DisallowMultipleComponent]
	public sealed class LandingBarge : MonoBehaviour
	{
		[SerializeField] private string stopId = "";
		[SerializeField] private string stopName = "";
		[SerializeField] private string[] routeIds = Array.Empty<string>();

		[Header("Provenance")]
		[Tooltip("enc-feature (NOAA ENC outline) or derived-from-gtfs (estimated rectangle).")]
		[SerializeField] private string source = "";
		[Tooltip("low, medium or high, from the pipeline.")]
		[SerializeField] private string confidence = "";
		[SerializeField] private string verification = "";
		[SerializeField] private string encId = "";

		[Header("GTFS stop, WGS84 degrees and world metres")]
		[SerializeField] private double gtfsLatitude;
		[SerializeField] private double gtfsLongitude;
		[SerializeField] private double gtfsNorth;
		[SerializeField] private double gtfsEast;

		[Header("Berthing face, world metres")]
		[SerializeField] private double faceStartNorth;
		[SerializeField] private double faceStartEast;
		[SerializeField] private double faceEndNorth;
		[SerializeField] private double faceEndEast;
		[Tooltip("Outward normal of the face (into the water), degrees clockwise from true north.")]
		[SerializeField] private double faceHeadingDegrees;
		[SerializeField] private double outwardNormalNorth;
		[SerializeField] private double outwardNormalEast;

		[Header("Greybox")]
		[Tooltip("Barge deck height above the water, metres (greybox estimate).")]
		[SerializeField] private float freeboard;
		[Tooltip("Barge depth below the water, metres (greybox estimate).")]
		[SerializeField] private float draft;

		public string StopId => stopId;

		public string StopName => stopName;

		public string[] RouteIds => routeIds;

		public string Source => source;

		public string Confidence => confidence;

		public string Verification => verification;

		public string EncId => encId;

		public double GtfsLatitude => gtfsLatitude;

		public double GtfsLongitude => gtfsLongitude;

		public double GtfsNorth => gtfsNorth;

		public double GtfsEast => gtfsEast;

		public double FaceStartNorth => faceStartNorth;

		public double FaceStartEast => faceStartEast;

		public double FaceEndNorth => faceEndNorth;

		public double FaceEndEast => faceEndEast;

		public double FaceHeadingDegrees => faceHeadingDegrees;

		public double OutwardNormalNorth => outwardNormalNorth;

		public double OutwardNormalEast => outwardNormalEast;

		public float Freeboard => freeboard;

		public float Draft => draft;

		/// <summary>The label text: name, provenance and confidence flag.</summary>
		public string LabelText =>
			$"{stopName}\n{(source == "enc-feature" ? "ENC outline" : source == "derived-from-gtfs" ? "estimated from GTFS" : source)} | {confidence} confidence\n{verification}";

		/// <summary>Fills the component. Only the edit-time builder calls this.</summary>
		public void Assign(LandingBargeValues values)
		{
			stopId = values.StopId ?? "";
			stopName = values.StopName ?? "";
			routeIds = values.RouteIds ?? Array.Empty<string>();
			source = values.Source ?? "";
			confidence = values.Confidence ?? "";
			verification = values.Verification ?? "";
			encId = values.EncId ?? "";
			gtfsLatitude = values.GtfsLatitude;
			gtfsLongitude = values.GtfsLongitude;
			gtfsNorth = values.GtfsNorth;
			gtfsEast = values.GtfsEast;
			faceStartNorth = values.FaceStartNorth;
			faceStartEast = values.FaceStartEast;
			faceEndNorth = values.FaceEndNorth;
			faceEndEast = values.FaceEndEast;
			faceHeadingDegrees = values.FaceHeadingDegrees;
			outwardNormalNorth = values.OutwardNormalNorth;
			outwardNormalEast = values.OutwardNormalEast;
			freeboard = values.Freeboard;
			draft = values.Draft;
		}
	}

	/// <summary>Plain values for <see cref="LandingBarge.Assign"/>.</summary>
	public sealed class LandingBargeValues
	{
		public string StopId;
		public string StopName;
		public string[] RouteIds;
		public string Source;
		public string Confidence;
		public string Verification;
		public string EncId;
		public double GtfsLatitude;
		public double GtfsLongitude;
		public double GtfsNorth;
		public double GtfsEast;
		public double FaceStartNorth;
		public double FaceStartEast;
		public double FaceEndNorth;
		public double FaceEndEast;
		public double FaceHeadingDegrees;
		public double OutwardNormalNorth;
		public double OutwardNormalEast;
		public float Freeboard;
		public float Draft;
	}
}
