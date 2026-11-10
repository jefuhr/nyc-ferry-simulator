using System.Linq;
using FerrySim.Controls;
using NUnit.Framework;

namespace FerrySim.UI.Tests.SettingsHud
{
	/// <summary>
	/// The options menu against a fake <see cref="IBindingService"/>, driving its commit methods (EditMode has no
	/// rendered panel). Failure modes guarded: a rebindable row missing, shown under the wrong scheme or map, or showing
	/// another slot's key; a refused rebind applying anything or not naming the holder; a cancelled rebind left pending;
	/// a reset that silently changes another action; reset all not reachable; a settings control that does not write the
	/// store, or writes the wrong field; the HUD page offering what cannot be chosen or deleted.
	/// </summary>
	public class OptionsMenuTests
	{
		private FakeBindingService _bindings;
		private SettingsStore _settings;
		private HudLayoutStore _layouts;
		private HudController _hud;
		private OptionsMenu _menu;

		[SetUp]
		public void SetUp()
		{
			_bindings = new FakeBindingService();
			_settings = SettingsStore.InMemory();
			_layouts = HudLayoutStore.InMemory();
			_hud = new HudController(HudWidgetRegistry.Default, _settings, _layouts);
			_menu = new OptionsMenu();
			_menu.Bind(_bindings, _settings, _hud);
		}

		[Test]
		public void Controls_ListsEveryRowOfTheScheme_GroupedByMap_WithItsKey()
		{
			_menu.ShowPage(OptionsPage.Controls);
			Assert.That(_menu.Scheme, Is.EqualTo("Keyboard"));
			var rows = _menu.BindingRows;
			var expected = _bindings.Rows.Where(row => row.Scheme == "Keyboard").ToArray();
			Assert.That(rows.Select(row => row.Row.ActionId + row.Row.Slot), Is.EqualTo(expected.Select(row => row.ActionId + row.Slot)));
			Assert.That(_menu.GroupNames, Is.EqualTo(new[] { "Helm", "Camera", "Alarms", "Interface", "Audio" }));
			Assert.That(Row(ActionIds.Horn).KeyText, Is.EqualTo("H"));
			// Two slots of one action show their own key each.
			Assert.That(Row(ActionIds.NfuJog, 0).KeyText, Is.EqualTo("F6"));
			Assert.That(Row(ActionIds.NfuJog, 1).KeyText, Is.EqualTo("F7"));
			Assert.That(Row(ActionIds.HelmStep, 1).KeyText, Is.EqualTo("Right Arrow"));

			_menu.SetScheme("Gamepad");
			Assert.That(_menu.BindingRows.Select(row => row.Row.ActionId), Is.EqualTo(new[] { ActionIds.Horn }));
			Assert.That(_menu.BindingRows[0].KeyText, Is.EqualTo("Button South"));
			_menu.SetScheme("Joystick");
			Assert.That(_menu.BindingRows[0].KeyText, Is.EqualTo(OptionsMenu.Unbound));
		}

		[Test]
		public void ARebind_ThatIsApplied_ShowsTheNewKey()
		{
			_menu.BeginRebind(Row(ActionIds.Horn));
			Assert.That(_bindings.LastBegun, Is.EqualTo(ActionIds.Horn + "|0|Keyboard"));
			Assert.That(_menu.Rebinding, Is.True);
			Assert.That(Row(ActionIds.Horn).KeyText, Is.EqualTo(OptionsMenu.PressAKey));
			_bindings.SetKey(ActionIds.Horn, "Keyboard", "F11");
			_bindings.Finish(new RebindOutcome(RebindResult.Applied, null));
			Assert.That(_menu.Rebinding, Is.False);
			Assert.That(Row(ActionIds.Horn).KeyText, Is.EqualTo("F11"));
			Assert.That(_menu.Message, Does.Contain("Horn"));
		}

		[Test]
		public void ARebind_CanBeCancelled_AndChangesNothing()
		{
			_menu.BeginRebind(Row(ActionIds.Horn));
			_menu.CancelRebind();
			Assert.That(_bindings.Cancels, Is.EqualTo(1));
			Assert.That(_menu.Rebinding, Is.False);
			Assert.That(Row(ActionIds.Horn).KeyText, Is.EqualTo("H"));
			Assert.That(_menu.Message, Does.Contain("cancelled").IgnoreCase);
		}

		[Test]
		public void ARefusedRebind_NamesTheActionInTheWay_AndChangesNothing()
		{
			_menu.BeginRebind(Row(ActionIds.Horn));
			_bindings.Finish(new RebindOutcome(RebindResult.Conflict, ActionIds.Pause));
			Assert.That(Row(ActionIds.Horn).KeyText, Is.EqualTo("H"));
			Assert.That(_menu.Message, Does.Contain("Pause"));
			Assert.That(_menu.Message, Does.Contain("nothing changed").IgnoreCase);

			_menu.BeginRebind(Row(ActionIds.Horn));
			_bindings.Finish(new RebindOutcome(RebindResult.Conflict, "Interface/Navigation"));
			Assert.That(_menu.Message, Does.Contain("menu navigation"));
		}

		[Test]
		public void Reset_ThatIsRefused_OffersResetThatOneToo_WhichResetsBoth()
		{
			_bindings.RefuseReset[ActionIds.Horn] = ActionIds.BowView;
			_menu.ResetAction(ActionIds.Horn);
			Assert.That(_menu.PendingReset, Is.Not.Null);
			Assert.That(_menu.PendingReset.ActionId, Is.EqualTo(ActionIds.Horn));
			Assert.That(_menu.PendingReset.ConflictActionId, Is.EqualTo(ActionIds.BowView));
			Assert.That(_menu.Message, Does.Contain("Bow View"));
			Assert.That(_bindings.ResetCalls, Is.EqualTo(new[] { ActionIds.Horn }), "nothing else was reset yet");

			_menu.ResolvePendingReset(ResetChoice.ResetThatOneToo);
			Assert.That(_bindings.ResetCalls, Is.EqualTo(new[] { ActionIds.Horn, ActionIds.BowView, ActionIds.Horn }));
			Assert.That(_menu.PendingReset, Is.Null);
			Assert.That(_bindings.ResetAlls, Is.Zero);
		}

		[Test]
		public void Reset_ThatIsRefused_CanResetAll_OrBeLeftAlone()
		{
			_bindings.RefuseReset[ActionIds.Horn] = ActionIds.BowView;
			_menu.ResetAction(ActionIds.Horn);
			_menu.ResolvePendingReset(ResetChoice.Cancel);
			Assert.That(_menu.PendingReset, Is.Null);
			Assert.That(_bindings.ResetAlls, Is.Zero);
			Assert.That(_bindings.ResetCalls, Is.EqualTo(new[] { ActionIds.Horn }));

			_menu.ResetAction(ActionIds.Horn);
			_menu.ResolvePendingReset(ResetChoice.ResetAll);
			Assert.That(_bindings.ResetAlls, Is.EqualTo(1));
			Assert.That(_menu.PendingReset, Is.Null);
		}

		[Test]
		public void Reset_ThatWorks_AndResetAll_AreReachable()
		{
			_menu.ResetAction(ActionIds.Mute);
			Assert.That(_menu.PendingReset, Is.Null);
			Assert.That(_bindings.ResetCalls, Is.EqualTo(new[] { ActionIds.Mute }));
			_menu.ResetAllBindings();
			Assert.That(_bindings.ResetAlls, Is.EqualTo(1));
		}

		[Test]
		public void TheKeybindPopupMode_WritesTheStore()
		{
			Assert.That(_menu.KeybindHints.Value, Is.EqualTo((int)KeybindHintMode.Hold));
			_menu.KeybindHints.Adjust(1);
			Assert.That(_settings.KeybindHints, Is.EqualTo(KeybindHintMode.Always));
			_menu.KeybindHints.Select((int)KeybindHintMode.Off);
			Assert.That(_settings.KeybindHints, Is.EqualTo(KeybindHintMode.Off));
		}

		[Test]
		public void EveryDisplayControl_WritesTheStore()
		{
			_menu.Palette.Select((int)PaletteSetting.Night);
			Assert.That(_settings.Palette, Is.EqualTo(PaletteSetting.Night));
			_menu.DistanceUnit.Adjust(1);
			Assert.That(_settings.DistanceUnit, Is.EqualTo(DistanceUnit.Metres));
			_menu.DepthAlarm.Adjust(1);
			Assert.That(_settings.DepthAlarmMetres, Is.EqualTo(2.0).Within(1e-9), "one step is 0.5 m in metres, snapped from 1.524");
			_menu.DistanceUnit.Adjust(-1);
			_menu.DepthAlarm.Adjust(-1);
			Assert.That(DisplayUnits.Distance(_settings.DepthAlarmMetres, DistanceUnit.Feet), Is.EqualTo(6.0).Within(1e-6), "one step is 1 ft in feet, snapped");
			_menu.VectorMinutes.Adjust(1);
			Assert.That(_settings.VectorMinutes, Is.EqualTo(2.5));
			_menu.Predictor.Adjust(1);
			Assert.That(_settings.Predictor, Is.EqualTo(PredictorKind.Curved));
			_menu.ConsoleScreens.Activate();
			Assert.That(_settings.ConsoleScreens, Is.False);
		}

		[Test]
		public void EveryAudioControl_WritesTheStore()
		{
			_menu.MasterVolume.Adjust(-1);
			Assert.That(_settings.MasterVolume, Is.EqualTo(0.95f).Within(1e-6f));
			_menu.Mute.Activate();
			Assert.That(_settings.Muted, Is.True);
			_menu.AlarmVolume.Adjust(-2);
			Assert.That(_settings.AlarmVolume, Is.EqualTo(0.9f).Within(1e-6f));
		}

		[Test]
		public void TheControls_FollowTheStore_WhenItChangesElsewhere()
		{
			_settings.Muted = true;
			_settings.DistanceUnit = DistanceUnit.Metres;
			Assert.That(_menu.Mute.Value, Is.True);
			Assert.That(_menu.DistanceUnit.Value, Is.EqualTo((int)DistanceUnit.Metres));
			Assert.That(_menu.DepthAlarm.ValueText, Does.EndWith("m"));
		}

		[Test]
		public void TheHudPage_ChoosesPresetsAndSavedLayouts_EntersEditMode_AndDeletesOnlySavedOnes()
		{
			_layouts.Put(new HudLayoutEntry { name = "mine", basedOn = HudPresets.Minimal });
			Assert.That(_menu.Layout.Options, Is.EqualTo(new[] { "console", "classic", "minimal", "everything", "mine" }));
			Assert.That(_menu.Layout.Value, Is.EqualTo(1), "classic is chosen at first launch");
			Assert.That(_menu.DeleteLayout.enabledSelf, Is.False, "a preset cannot be deleted");
			_menu.Layout.Select(4);
			Assert.That(_settings.HudLayout, Is.EqualTo("mine"));
			Assert.That(_hud.Current.Name, Is.EqualTo("mine"));
			Assert.That(_menu.DeleteLayout.enabledSelf, Is.True);

			var editRequests = 0;
			_menu.EditHudRequested += () => editRequests++;
			_menu.RequestHudEdit();
			Assert.That(editRequests, Is.EqualTo(1));

			_menu.DeleteChosenLayout();
			Assert.That(_menu.PendingDelete, Is.EqualTo("mine"), "deleting asks first");
			Assert.That(_layouts.Find("mine"), Is.Not.Null);
			_menu.ConfirmDelete(true);
			Assert.That(_layouts.Find("mine"), Is.Null);
			Assert.That(_settings.HudLayout, Is.EqualTo(HudPresets.Minimal));
			Assert.That(_menu.Layout.Options.Count, Is.EqualTo(4));
		}

		[Test]
		public void Close_IsRaisedByTheCloseCommand()
		{
			var closes = 0;
			_menu.CloseRequested += () => closes++;
			_menu.RequestClose();
			Assert.That(closes, Is.EqualTo(1));
		}

		[Test]
		public void ACloseWhileRebinding_CancelsTheRebindFirst()
		{
			var closes = 0;
			_menu.CloseRequested += () => closes++;
			_menu.BeginRebind(Row(ActionIds.Horn));
			_menu.Back();
			Assert.That(_bindings.Cancels, Is.EqualTo(1));
			Assert.That(closes, Is.Zero, "the first Back only cancels the rebind");
			_menu.Back();
			Assert.That(closes, Is.EqualTo(1));
		}

		[Test]
		public void WithoutABindingService_TheControlsPageSaysSo()
		{
			var menu = new OptionsMenu();
			menu.Bind(null, _settings, _hud);
			Assert.That(menu.BindingRows, Is.Empty);
			Assert.That(menu.Message, Is.EqualTo(OptionsMenu.NoBindings));
		}

		private BindingRowView Row(string actionId, int slot = 0) => _menu.BindingRows.First(row => row.Row.ActionId == actionId && row.Row.Slot == slot);
	}
}
