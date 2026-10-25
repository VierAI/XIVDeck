using System;
using System.Collections.Generic;
using XIVDeck.FFXIVPlugin.Base;
using static FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule;

namespace XIVDeck.FFXIVPlugin.Server;

/// <summary>
/// Resolves the icon id for a Stream Deck action. Reads game state, so call it from the framework thread.
/// </summary>
internal static class ActionIcons {
    private static readonly Dictionary<string, HotbarSlotType> Aliases = new() {
        { "Minion", HotbarSlotType.Companion },
        { "FashionAccessory", HotbarSlotType.Ornament }
    };

    /// <summary>
    /// Types the player can change in game (icon, name, existence). Everything else comes from game data and
    /// can't change while connected, so only these are re-checked by <see cref="Game.Watchers.EditableActionWatcher"/>.
    /// </summary>
    public static bool IsPlayerEditable(string type) {
        return type.Equals("Macro", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("GearSet", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryResolve(string type, int id, out int iconId) {
        return TryResolve(type, id, out iconId, out _);
    }

    public static bool TryResolve(string type, int id, out int iconId, out string? name) {
        iconId = 0;
        name = null;
        if (id < 0 || string.IsNullOrEmpty(type)) return false;

        if (type.Equals("Class", StringComparison.OrdinalIgnoreCase)) {
            if (id == 0) return false;
            iconId = 62100 + id;
            return true;
        }

        if (!Aliases.TryGetValue(type, out var slotType) && !Enum.TryParse(type, out slotType))
            return false;

        var strategies = XIVDeckPlugin.Instance.ActionDispatcher.GetStrategies();
        if (!strategies.ContainsKey(slotType)) return false;

        try {
            var action = strategies[slotType].GetExecutableActionById((uint)id);
            if (action == null) return false;

            iconId = action.IconId;
            if (IsPlayerEditable(type)) name = action.ActionName;
            return true;
        } catch (Exception ex) {
            Injections.PluginLog.Debug(ex, $"Couldn't resolve icon for {type} {id}");
            return false;
        }
    }
}
