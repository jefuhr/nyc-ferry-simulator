using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace FerrySim.UI.Displays
{
	/// <summary>
	/// Finds a skin by display and id: first <c>&lt;private folder&gt;/&lt;display&gt;/&lt;id&gt;.json</c> (by default a
	/// <c>skins/</c> folder under the persistent data path, which is never shipped), then the shipped resource
	/// <c>Resources/FerrySimUI/Skins/&lt;display&gt;/&lt;id&gt;.json</c>. A private skin that does not load, or whose
	/// contents name another display or id, is reported (<see cref="ProblemReported"/>, <see cref="Problems"/> and a
	/// warning in the log) and the shipped skin is used instead, so a bad private file never blanks a console.
	/// </summary>
	public sealed class DisplaySkinLibrary
	{
		public const string ResourceFolder = "FerrySimUI/Skins";

		private static readonly Regex SafeName = new Regex("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant);

		private readonly IReadOnlyCollection<string> _alarmIds;
		private readonly string _privateFolder;
		private readonly List<string> _problems = new List<string>();

		/// <summary>A library that looks in <see cref="DefaultPrivateFolder"/> first.</summary>
		public DisplaySkinLibrary(IReadOnlyCollection<string> alarmIds)
			: this(alarmIds, DefaultPrivateFolder)
		{
		}

		/// <param name="alarmIds">The alarm ids the vessel defines (<see cref="DisplaySkinAlarms"/>).</param>
		/// <param name="privateFolder">Where private skins live; null to use the shipped skins only.</param>
		public DisplaySkinLibrary(IReadOnlyCollection<string> alarmIds, string privateFolder)
		{
			_alarmIds = alarmIds ?? throw new ArgumentNullException(nameof(alarmIds));
			_privateFolder = privateFolder;
		}

		/// <summary><c>skins/</c> under <see cref="Application.persistentDataPath"/>.</summary>
		public static string DefaultPrivateFolder => Path.Combine(Application.persistentDataPath, "skins");

		/// <summary>Where the last skin loaded came from: <c>private:&lt;path&gt;</c> or <c>shipped:&lt;resource&gt;</c>.</summary>
		public string LastSource { get; private set; }

		/// <summary>Every private skin that was reported and passed over, in order.</summary>
		public IReadOnlyList<string> Problems => _problems;

		public event Action<string> ProblemReported;

		/// <summary>The skin, or <see cref="DisplaySkinException"/> when neither a private nor a shipped one loads.</summary>
		public DisplaySkin Load(string display, string id)
		{
			if (TryLoad(display, id, out var skin, out var error))
			{
				return skin;
			}

			throw new DisplaySkinException(error);
		}

		public bool TryLoad(string display, string id, out DisplaySkin skin) => TryLoad(display, id, out skin, out _);

		public bool TryLoad(string display, string id, out DisplaySkin skin, out string error)
		{
			skin = null;
			if (display == null || id == null || !SafeName.IsMatch(display) || !SafeName.IsMatch(id))
			{
				error = "skin names must be lower-case words and dashes, not '" + display + "/" + id + "'";
				return false;
			}

			if (_privateFolder != null)
			{
				var path = Path.Combine(_privateFolder, display, id + ".json");
				if (File.Exists(path) && TryPrivate(path, display, id, out skin))
				{
					LastSource = "private:" + path;
					error = null;
					return true;
				}
			}

			var resource = ResourceFolder + "/" + display + "/" + id;
			var asset = Resources.Load<TextAsset>(resource);
			if (asset == null)
			{
				error = "no skin " + display + "/" + id + (_privateFolder != null ? " in " + _privateFolder + " or" : "") + " in Resources/" + resource + ".json";
				return false;
			}

			try
			{
				skin = DisplaySkin.Parse(asset.text, _alarmIds);
			}
			catch (DisplaySkinException exception)
			{
				// A shipped skin is tested; failing here is a bug, not a user's file.
				throw new DisplaySkinException("shipped skin Resources/" + resource + ".json does not load: " + exception.Message, exception.ElementNumber);
			}
			finally
			{
				Resources.UnloadAsset(asset);
			}

			LastSource = "shipped:" + resource;
			error = null;
			return true;
		}

		private bool TryPrivate(string path, string display, string id, out DisplaySkin skin)
		{
			skin = null;
			string json;
			try
			{
				json = File.ReadAllText(path);
			}
			catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
			{
				Report(path, "could not be read: " + exception.Message);
				return false;
			}

			if (!DisplaySkin.TryParse(json, _alarmIds, out var parsed, out var error))
			{
				Report(path, error);
				return false;
			}

			if (parsed.Display != display || parsed.Id != id)
			{
				Report(path, "names display '" + parsed.Display + "' and id '" + parsed.Id + "', but its path says " + display + "/" + id);
				return false;
			}

			skin = parsed;
			return true;
		}

		private void Report(string path, string problem)
		{
			var message = "Private display skin " + path + " was not used: " + problem + ". Using the shipped skin.";
			_problems.Add(message);
			Debug.LogWarning(message);
			ProblemReported?.Invoke(message);
		}
	}
}
