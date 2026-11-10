using System;
using System.IO;

namespace FerrySim.UI.Tests.SettingsHud
{
	/// <summary>A fresh folder under the system temporary folder, deleted on dispose, so no test touches the real persistent data folder.</summary>
	internal sealed class TempFolder : IDisposable
	{
		public TempFolder()
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ferrysim-settings-tests-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Path);
		}

		public string Path { get; }

		public string File(string name) => System.IO.Path.Combine(Path, name);

		public void Dispose()
		{
			try
			{
				if (Directory.Exists(Path))
				{
					Directory.Delete(Path, true);
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}
}
