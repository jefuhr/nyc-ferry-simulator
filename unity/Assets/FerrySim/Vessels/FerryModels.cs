using UnityEngine;

namespace FerrySim.Vessels
{
	/// <summary>Shared FBX prefabs. Loading and mount discovery happen only when a vessel is built.</summary>
	public static class FerryModels
	{
		public const string RootName = "FerryModel";

		public static GameObject Load(string classId, int detail) =>
			(classId == "t-class" || classId == "k-class") && (detail == 0 || detail == 1)
				? Resources.Load<GameObject>("FerrySimVessels/" + classId + "/LOD" + detail) : null;

		public static FerryModel Create(Transform parent, string classId, int detail, double length, double beam)
		{
			var prefab = Load(classId, detail);
			if (prefab == null) { return null; }
			var host = Object.Instantiate(prefab, parent, false);
			host.name = RootName;
			var model = host.GetComponent<FerryModel>();
			host.transform.localScale = new Vector3((float)beam == model.Beam ? 1f : (float)(beam / model.Beam),
				1f, (float)length == model.Length ? 1f : (float)(length / model.Length));
			return model;
		}

		/// <summary>Exact audited identities, never inferred from names or dimensions. Source: calibration/identity-audit.csv.</summary>
		public static string ClassForMmsi(int mmsi)
		{
			switch (mmsi)
			{
				case 367776270:
				case 367778670:
				case 367779540:
				case 367779550:
				case 367782690:
				case 367784630:
				case 367784640:
				case 367790830:
				case 367797260:
				case 367798420:
				case 367798430:
				case 368004120:
				case 368025020:
				case 368141510:
					return "t-class";
				case 368129810:
				case 368135270:
					return "k-class";
				default: return null;
			}
		}
	}
}
