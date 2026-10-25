using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using static FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule;
using XIVDeck.FFXIVPlugin.Base;
using XIVDeck.FFXIVPlugin.Game.Managers;
using XIVDeck.FFXIVPlugin.Server.Types;

namespace XIVDeck.FFXIVPlugin.Game.Watchers;

public class HotbarWatcher : IDisposable {
    // 10 standard + 8 cross hotbars (RaptureHotbarModule.Hotbars is FixedSizeArray18); ids 0-17 like HotbarController
    private const int HotbarCount = 18;
    private const int SlotCount = 16;

    private readonly HotbarSlot[,] _hotbarCache = new HotbarSlot[HotbarCount, SlotCount];

    public HotbarWatcher() {
        Injections.Framework.Update += this.OnGameUpdate;
    }

    /// <summary>
    /// Reads one slot. Call from the framework thread.
    /// </summary>
    public static unsafe int ReadIcon(int hotbarId, int slotId) {
        var ui = Framework.Instance()->GetUIModule();
        if (ui == null) return 0;

        var module = ui->GetRaptureHotbarModule();
        if (module == null) return 0;

        var gameSlot = module->GetSlotById((uint)hotbarId, (uint)slotId);
        if (gameSlot == null) return 0;

        return IconFor(gameSlot);
    }

    /// <summary>
    /// Reads every watched slot, including empties, and seeds the diff cache.
    /// Call from the framework thread. Does not encode PNGs.
    /// </summary>
    public unsafe List<MicroHotbarSlot> CaptureSnapshot() {
        var slots = new List<MicroHotbarSlot>(HotbarCount * SlotCount);
        var ui = Framework.Instance()->GetUIModule();
        if (ui == null) return slots;

        var hotbarModule = ui->GetRaptureHotbarModule();
        if (hotbarModule == null) return slots;

        for (var hotbarId = 0; hotbarId < HotbarCount; hotbarId++) {
            ref var hotbar = ref hotbarModule->Hotbars[hotbarId];

            for (var slotId = 0; slotId < SlotCount; slotId++) {
                var gameSlot = hotbar.GetHotbarSlot((uint)slotId);
                var iconId = IconFor(gameSlot);
                this.Remember(hotbarId, slotId, gameSlot, iconId);
                slots.Add(new MicroHotbarSlot(hotbarId, slotId, iconId));
            }
        }

        return slots;
    }

    /// <summary>
    /// Capture on the framework thread, then push slot ids and any missing PNGs off that thread.
    /// </summary>
    public void PublishToClient(string contextId) {
        _ = System.Threading.Tasks.Task.Run(async () => {
            try {
                var slots = await Injections.Framework.RunOnFrameworkThread(this.CaptureSnapshot);
                XIVDeckPlugin.Instance.Server.SendHotbar(contextId, slots);
            } catch (Exception ex) {
                Injections.PluginLog.Error(ex, "Couldn't snapshot hotbars for a Stream Deck client");
            }
        });
    }

    public void PublishAll() {
        if (!Injections.Framework.IsInFrameworkUpdateThread) {
            _ = Injections.Framework.RunOnFrameworkThread(this.PublishAll);
            return;
        }

        try {
            var slots = this.CaptureSnapshot();
            XIVDeckPlugin.Instance.Server.BroadcastHotbar(slots);
        } catch (Exception ex) {
            Injections.PluginLog.Error(ex, "Couldn't snapshot hotbars");
        }
    }

    private unsafe void OnGameUpdate(IFramework framework) {
        var ui = Framework.Instance()->GetUIModule();
        if (ui == null) return;

        var hotbarModule = ui->GetRaptureHotbarModule();
        if (hotbarModule == null) return;

        List<MicroHotbarSlot> updatedSlots = new();

        for (var hotbarId = 0; hotbarId < HotbarCount; hotbarId++) {
            ref var hotbar = ref hotbarModule->Hotbars[hotbarId];

            for (var slotId = 0; slotId < SlotCount; slotId++) {
                var gameSlot = hotbar.GetHotbarSlot((uint)slotId);
                var cachedSlot = this._hotbarCache[hotbarId, slotId];

                // IconB is the cheap appearance check. Skip the icon lookup when nothing about the slot changed.
                HotbarManager.CalcBForSlot(gameSlot, out var calcApparentType, out var calcApparentId);

                if (gameSlot->CommandId == cachedSlot.CommandId &&
                    gameSlot->CommandType == cachedSlot.CommandType &&
                    calcApparentType == cachedSlot.ApparentSlotType &&
                    calcApparentId == cachedSlot.ApparentActionId) continue;

                var iconId = IconFor(gameSlot);
                var changed = iconId != (int)cachedSlot.IconId;
                this.Remember(hotbarId, slotId, gameSlot, iconId, calcApparentType, calcApparentId);

                if (changed)
                    updatedSlots.Add(new MicroHotbarSlot(hotbarId, slotId, iconId));
            }
        }

        if (updatedSlots.Count > 0) {
            Injections.PluginLog.Debug($"Detected a change to {updatedSlots.Count} hotbar slot(s)");
            XIVDeckPlugin.Instance.Server.BroadcastHotbar(updatedSlots);
        }
    }

    private unsafe void Remember(
        int hotbarId,
        int slotId,
        HotbarSlot* gameSlot,
        int iconId,
        HotbarSlotType? apparentType = null,
        uint? apparentId = null) {
        if (apparentType == null || apparentId == null) {
            HotbarManager.CalcBForSlot(gameSlot, out var calculatedType, out var calculatedId);
            apparentType ??= calculatedType;
            apparentId ??= calculatedId;
        }

        this._hotbarCache[hotbarId, slotId] = new HotbarSlot {
            CommandId = gameSlot->CommandId,
            IconId = (uint)iconId,
            CommandType = gameSlot->CommandType,
            ApparentSlotType = apparentType.Value,
            ApparentActionId = apparentId.Value
        };
    }

    private static unsafe int IconFor(HotbarSlot* gameSlot) {
        // same as HotbarController/upstream: an empty slot is icon 0, the deck paints it blank
        if (gameSlot->CommandType == HotbarSlotType.Empty) return 0;

        HotbarManager.CalcBForSlot(gameSlot, out var apparentType, out var apparentId);
        return (int)(uint)gameSlot->GetIconIdForSlot(apparentType, apparentId);
    }

    public void Dispose() {
        Injections.Framework.Update -= this.OnGameUpdate;

        GC.SuppressFinalize(this);
    }
}
