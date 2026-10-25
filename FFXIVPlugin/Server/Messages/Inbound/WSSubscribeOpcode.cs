using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EmbedIO.WebSockets;
using Newtonsoft.Json;
using XIVDeck.FFXIVPlugin.Base;
using XIVDeck.FFXIVPlugin.Game.Watchers;
using XIVDeck.FFXIVPlugin.Server.Helpers;
using XIVDeck.FFXIVPlugin.Server.Types;

namespace XIVDeck.FFXIVPlugin.Server.Messages.Inbound;

[WSOpcode("subscribe")]
public class WSSubscribeOpcode : BaseInboundMessage {
    private const int MaxItems = 128;

    [JsonProperty("hotbars")] public List<HotbarRef>? Hotbars { get; set; }
    [JsonProperty("actions")] public List<ActionRef>? Actions { get; set; }

    /// <summary>
    /// Icon ids the deck knows about but has no image for. Sent even if this connection was sent them before.
    /// </summary>
    [JsonProperty("icons")] public List<int>? Icons { get; set; }

    public class HotbarRef {
        [JsonProperty("hotbarId")] public int HotbarId { get; set; }
        [JsonProperty("slotId")] public int SlotId { get; set; }
    }

    public class ActionRef {
        [JsonProperty("type")] public string? Type { get; set; }
        [JsonProperty("id")] public int Id { get; set; }
    }

    public override Task Process(IWebSocketContext context) {
        // Don't hold the socket's receive loop while waiting for a framework tick: with one awaited hop per
        // message, a burst of subscribes (fast page flips) queued up behind each other one frame at a time.
        _ = Task.Run(async () => {
            try {
                await this.ProcessAsync(context);
            } catch (Exception ex) {
                Injections.PluginLog.Error(ex, "Stream Deck subscribe failed");
            }
        });

        return Task.CompletedTask;
    }

    private async Task ProcessAsync(IWebSocketContext context) {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var hotbars = (this.Hotbars ?? []).Take(MaxItems).ToList();
        var actions = (this.Actions ?? [])
            .Where(action => !string.IsNullOrEmpty(action.Type))
            .Take(MaxItems)
            .ToList();

        var server = XIVDeckPlugin.Instance.Server;
        server.RememberActions(context.Id, actions.Select(action => (action.Type!, action.Id)).ToList());

        var resolved = await Injections.Framework.RunOnFrameworkThread(() => Resolve(hotbars, actions));
        var resolvedMs = started.ElapsedMilliseconds;

        if (resolved.Slots.Count > 0)
            server.SendHotbar(context.Id, resolved.Slots);

        if (resolved.Actions.Count > 0)
            server.SendActionIcons(context.Id, resolved.Actions);

        var icons = (this.Icons ?? []).Where(id => id > 0).Distinct().Take(MaxItems).ToList();
        if (icons.Count > 0)
            server.SendIcons(context.Id, icons);

        var unresolved = actions.Count - resolved.Actions.Count;
        if (hotbars.Count > 0 || actions.Count > 0 || icons.Count > 0)
            Injections.PluginLog.Debug(
                $"Stream Deck subscribe: {hotbars.Count} slot(s), {actions.Count} action(s) ({unresolved} unresolved, " +
                $"retried at login), {icons.Count} icon resend(s); resolved in {resolvedMs}ms");
    }

    private static (List<MicroHotbarSlot> Slots, List<ActionIconDto> Actions) Resolve(
        List<HotbarRef> hotbars,
        List<ActionRef> actions) {
        var slots = new List<MicroHotbarSlot>(hotbars.Count);
        foreach (var hotbar in hotbars) {
            if (hotbar.HotbarId is < 0 or > 17 || hotbar.SlotId is < 0 or > 15) continue;

            try {
                slots.Add(new MicroHotbarSlot(
                    hotbar.HotbarId,
                    hotbar.SlotId,
                    HotbarWatcher.ReadIcon(hotbar.HotbarId, hotbar.SlotId)));
            } catch (Exception ex) {
                Injections.PluginLog.Debug(ex, $"Couldn't read hotbar {hotbar.HotbarId}/{hotbar.SlotId}");
            }
        }

        var icons = new List<ActionIconDto>(actions.Count);
        foreach (var action in actions) {
            if (!ActionIcons.TryResolve(action.Type!, action.Id, out var iconId, out var name)) continue;
            icons.Add(new ActionIconDto(action.Type!, action.Id, iconId, name));
        }

        return (slots, icons);
    }
}
