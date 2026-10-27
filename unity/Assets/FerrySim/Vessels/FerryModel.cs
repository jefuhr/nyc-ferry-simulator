using UnityEngine;

namespace FerrySim.Vessels
{
	/// <summary>Authored metre dimensions and named mount points. No per-frame work or physics.</summary>
	public sealed class FerryModel : MonoBehaviour
	{
		public string ClassId;
		public int Detail;
		public float Length;
		public float Beam;
		public float Draft;
		public float Height;

		/// <summary>Mount positions are metres from midships at the design waterline: x starboard, y up, z forward.</summary>
		public Transform Mount(string name) => transform.Find(name);
	}
}
