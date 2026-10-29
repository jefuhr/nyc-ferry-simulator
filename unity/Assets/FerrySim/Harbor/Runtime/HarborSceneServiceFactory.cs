using UnityEngine;

namespace FerrySim.Harbor
{
	/// <summary>Coordinator-supplied component. Its Create method returns verified services, or throws a named data error.</summary>
	public abstract class HarborSceneServiceFactory : MonoBehaviour
	{
		public abstract IHarborSceneServices Create(HarborSceneContext context);
	}
}
