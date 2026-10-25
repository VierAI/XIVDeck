using System;
using System.Diagnostics;
using Dalamud.Plugin.Services;
using XIVDeck.FFXIVPlugin.Base;

namespace XIVDeck.FFXIVPlugin.Game.Watchers;

/// <summary>
/// Keeps Stream Deck keys for player-editable entries (macros, gearsets) in sync. The gearset-save and macro-save
/// hooks in <see cref="GameHooks"/> push immediately on an edit; this throttled diff of only the entries a deck asked
/// for catches everything those hooks don't see: /micon icons that follow another action/item/gearset (e.g. an
/// action upgrading with level, a gearset's job changing), gearset job/class changes that don't write the file,
/// gearsets created or deleted, and anything that resolved late after login.
/// </summary>
public class EditableActionWatcher : IDisposable {
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly Stopwatch _sinceCheck = Stopwatch.StartNew();
    private bool _scanning; // one pass is spread over several frames with a small per-frame budget

    public EditableActionWatcher() {
        Injections.Framework.Update += this.OnUpdate;
    }

    private void OnUpdate(IFramework framework) {
        if (!this._scanning) {
            if (this._sinceCheck.Elapsed < Interval) return;
            this._sinceCheck.Restart();
        }

        // macro/gearset modules only hold the character's data once logged in
        if (!Injections.ClientState.IsLoggedIn) { this._scanning = false; return; }

        try {
            // Server is assigned after the watchers in the plugin constructor
            this._scanning = XIVDeckPlugin.Instance?.Server?.PushEditableChanges() ?? false;
        } catch (Exception ex) {
            this._scanning = false;
            Injections.PluginLog.Warning(ex, "Editable deck entry check failed");
        }
    }

    public void Dispose() {
        Injections.Framework.Update -= this.OnUpdate;
        GC.SuppressFinalize(this);
    }
}
