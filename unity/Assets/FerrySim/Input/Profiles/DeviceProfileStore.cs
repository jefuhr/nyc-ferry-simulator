using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FerrySim.Controls.Profiles
{
	/// <summary>Versioned per-device JSON. A rejected load never partially applies a file.</summary>
	public sealed class DeviceProfileStore
	{
		public const int CurrentVersion = 1;
		public const string FileName = "input-profiles-v1.json";
		[Serializable]
		private sealed class Document
		{
			public int version;
			public List<DeviceProfile> devices;
		}
		private readonly string _path;
		private string _routingError = "";
		private readonly List<DeviceProfile> _profiles = new List<DeviceProfile>();
		public string LastError { get; private set; } = "";
		public string Path => _path;
		public event Action Changed;
		public static string DefaultPath => System.IO.Path.Combine(Application.persistentDataPath, FileName);
		public DeviceProfileStore(string path) => _path = path ?? throw new ArgumentNullException(nameof(path));

		internal void ReportRoutingError(string reason)
		{
			_routingError = "Input assignment ignored: " + reason;
			LastError = _routingError;
		}
		internal void ClearRoutingError()
		{
			if (LastError == _routingError) LastError = "";
			_routingError = "";
		}

		// Publish only after persistence succeeds. Failed saves cannot transiently take over a target
		// or lose an existing keyboard/device owner while the previous profile is restored.
		internal bool SaveProfile(DeviceProfile profile) => Persist(profile, profile.identity);
		internal bool ResetAndSave(string identity) => Persist(null, identity);
		private bool Persist(DeviceProfile profile, string identity)
		{
			if (profile != null && !profile.IsValid) throw new ArgumentException("A valid profile is required.", nameof(profile));
			var previous = _profiles.ToArray();
			_profiles.RemoveAll(item => item.identity == identity);
			if (profile != null) _profiles.Add(profile.Copy());
			if (!Save())
			{
				_profiles.Clear(); _profiles.AddRange(previous);
				return false;
			}
			Changed?.Invoke();
			return true;
		}

		public bool Load()
		{
			_profiles.Clear();
			LastError = "";
			try
			{
				if (!File.Exists(_path)) { Changed?.Invoke(); return true; }
				var document = JsonUtility.FromJson<Document>(File.ReadAllText(_path));
				if (document == null || document.version != CurrentVersion || document.devices == null)
					throw new InvalidDataException("Unsupported or missing input profile version (expected 1).");
				var identities = new HashSet<string>(StringComparer.Ordinal);
				foreach (var profile in document.devices)
				{
					if (profile == null || !profile.IsValid || !identities.Add(profile.identity))
						throw new InvalidDataException("Invalid or duplicate device profile.");
				}
				_profiles.AddRange(document.devices);
				Changed?.Invoke();
				return true;
			}
			catch (Exception error) when (error is InvalidDataException || error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
			{
				LastError = "Input profiles ignored: " + error.Message;
				Changed?.Invoke();
				return false;
			}
		}

		public bool Save()
		{
			LastError = "";
			var temporary = _path + ".tmp";
			try
			{
				var directory = System.IO.Path.GetDirectoryName(_path);
				if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
				File.WriteAllText(temporary, JsonUtility.ToJson(new Document { version = CurrentVersion, devices = _profiles }, true));
				if (File.Exists(_path)) File.Replace(temporary, _path, null);
				else File.Move(temporary, _path);
				LastError = _routingError;
				return true;
			}
			catch (Exception error) when (error is InvalidDataException || error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
			{
				LastError = "Could not save input profiles: " + error.Message;
				return false;
			}
			finally
			{
				try { if (File.Exists(temporary)) File.Delete(temporary); }
				catch (IOException) { }
				catch (UnauthorizedAccessException) { }
			}
		}

		public DeviceProfile Find(string identity) => _profiles.Find(profile => profile.identity == identity)?.Copy();
		public void Set(DeviceProfile profile)
		{
			if (profile == null || !profile.IsValid) throw new ArgumentException("A complete, valid device profile is required.", nameof(profile));
			_profiles.RemoveAll(item => item.identity == profile.identity);
			_profiles.Add(profile.Copy());
			Changed?.Invoke();
		}
		public void Reset(string identity)
		{
			_profiles.RemoveAll(profile => profile.identity == identity);
			Changed?.Invoke();
		}
	}
}
