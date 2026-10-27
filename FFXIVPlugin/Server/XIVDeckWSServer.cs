using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.WebSockets;
using Newtonsoft.Json;
using XIVDeck.FFXIVPlugin.Base;
using XIVDeck.FFXIVPlugin.Server.Helpers;
using XIVDeck.FFXIVPlugin.Server.Messages;
using XIVDeck.FFXIVPlugin.Server.Messages.Outbound;
using XIVDeck.FFXIVPlugin.Server.Types;
using XIVDeck.FFXIVPlugin.Utils;

namespace XIVDeck.FFXIVPlugin.Server;

public class XIVDeckWSServer : WebSocketModule {
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly WSOpcodeWiring _wsModules = new();
    private readonly ConcurrentDictionary<string, DeckSession> _sessions = new();

    public XIVDeckWSServer(string urlPath) : base(urlPath, true) {
        this._wsModules.Autowire();
    }

    protected override async Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result) {
        // crash protection - prevents WS from taking down all of FFXIV if a message fails to decode
        try {
            await this._onMessage(context, buffer);
        } catch (Exception ex) {
            Injections.PluginLog.Error(ex, "Got an exception on websocket process");
        }
    }

    private async Task _onMessage(IWebSocketContext context, byte[] buffer) {
        var rawData = this.Encoding.GetString(buffer);
        var message = JsonConvert.DeserializeObject<BaseInboundMessage>(rawData);

        if (message == null) {
            Injections.PluginLog.Warning($"WebSocket message failed to deserialize to base: {rawData}");
            return;
        }

        var instance = this._wsModules.GetInstance(message.Opcode, rawData);

        if (instance == null) {
            Injections.PluginLog.Warning($"WebSocket message failed to deserialize to instance: {rawData}");
            return;
        }

        await instance.Process(context);
    }

    internal IReadOnlyList<IWebSocketContext> Connections => this.ActiveContexts;

    protected override Task OnClientConnectedAsync(IWebSocketContext context) {
        this._sessions.GetOrAdd(context.Id, _ => new DeckSession());
        return base.OnClientConnectedAsync(context);
    }

    protected override Task OnClientDisconnectedAsync(IWebSocketContext context) {
        this._sessions.TryRemove(context.Id, out _);
        return base.OnClientDisconnectedAsync(context);
    }

    internal void NoteMode(string contextId, bool wantsPush) {
        var session = this._sessions.GetOrAdd(contextId, _ => new DeckSession());
        session.WantsPush = wantsPush;
    }

    internal void RememberActions(string contextId, IReadOnlyList<(string Type, int Id)> actions) {
        if (actions.Count == 0) return;

        var session = this._sessions.GetOrAdd(contextId, _ => new DeckSession());
        lock (session.Gate) {
            foreach (var action in actions)
                session.Actions.Add(action);
        }
    }

    // Sessions are added on connect and removed on disconnect, so this is a lock-free connected-client check.
    internal bool HasClients => !this._sessions.IsEmpty;

    internal void BroadcastHotbar(IReadOnlyList<MicroHotbarSlot> slots) {
        var copy = slots as MicroHotbarSlot[] ?? slots.ToArray();
        _ = Task.Run(() => this.DeliverHotbarAsync(null, copy));
    }

    internal void SendHotbar(string contextId, IReadOnlyList<MicroHotbarSlot> slots) {
        var copy = slots as MicroHotbarSlot[] ?? slots.ToArray();
        _ = Task.Run(() => this.DeliverHotbarAsync(contextId, copy));
    }

    internal void SendActionIcons(string contextId, IReadOnlyList<ActionIconDto> actions) {
        var copy = actions as ActionIconDto[] ?? actions.ToArray();
        _ = Task.Run(() => this.DeliverActionsAsync([(contextId, copy)]));
    }

    /// <summary>
    /// Re-resolve remembered action subscriptions and push their icon ids (and any PNG the client lacks).
    /// A null type refreshes every subscription, e.g. at login when actions that couldn't resolve at the title
    /// screen (macros, gearsets, unlock-dependent actions) become resolvable.
    /// </summary>
    internal void RefreshSubscribedActions(string? actionType) {
        _ = Task.Run(async () => {
            try {
                var work = await Injections.Framework.RunOnFrameworkThread(() => this.ResolveSubscribed(actionType));
                await this.DeliverActionsAsync(work);
            } catch (Exception ex) {
                Injections.PluginLog.Error(ex, $"Couldn't refresh subscribed {actionType ?? "all"} icons");
            }
        });
    }

    /// <summary>
    /// Send these PNGs to one client even if it was sent them before. The deck asks for this when it knows an
    /// icon id but has no image for it.
    /// </summary>
    internal void SendIcons(string contextId, IReadOnlyList<int> iconIds) {
        var ids = iconIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return;

        _ = Task.Run(async () => {
            try {
                if (!this.HasPushTarget(contextId)) return;
                var urls = await IconPngCache.GetDataUrlsAsync(ids);

                using (await this._sendLock.UseWaitAsync()) {
                    foreach (var (context, session) in this.PushTargets(contextId)) {
                        lock (session.Gate) session.SentIcons.ExceptWith(ids);
                        await this.SendMissingIcons(context, session, ids, urls);
                    }
                }
            } catch (Exception ex) {
                Injections.PluginLog.Error(ex, "Requested icon push failed");
            }
        });
    }

    /// <summary>
    /// Forget which PNGs every client has. Pair with <see cref="IconPngCache.Clear"/> for a full redraw.
    /// </summary>
    internal void ForgetSentIcons() {
        foreach (var (_, session) in this._sessions) {
            lock (session.Gate) session.SentIcons.Clear();
        }
    }

    /// <summary>
    /// These icons changed on the game side (icon mod toggled). Re-send them to every client that already had them,
    /// so cached Stream Deck keys repaint without a reconnect.
    /// </summary>
    internal void ReplaceIcons(IReadOnlyList<int> iconIds) {
        var ids = iconIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return;

        _ = Task.Run(async () => {
            try {
                var perSession = new List<(IWebSocketContext Context, DeckSession Session, int[] Ids)>();
                foreach (var (context, session) in this.PushTargets(null)) {
                    int[] had;
                    lock (session.Gate) {
                        had = ids.Where(session.SentIcons.Contains).ToArray();
                        session.SentIcons.ExceptWith(had);
                    }

                    if (had.Length > 0) perSession.Add((context, session, had));
                }

                if (perSession.Count == 0) return;

                Injections.PluginLog.Debug($"Re-sending {ids.Length} modified icon(s) to Stream Deck");
                var urls = await IconPngCache.GetDataUrlsAsync(perSession.SelectMany(item => item.Ids));

                using (await this._sendLock.UseWaitAsync()) {
                    foreach (var (context, session, had) in perSession)
                        await this.SendMissingIcons(context, session, had, urls);
                }
            } catch (Exception ex) {
                Injections.PluginLog.Error(ex, "Modified icon push failed");
            }
        });
    }

    public void BroadcastMessage(BaseOutboundMessage message) {
        Task.Run(async () => {
            using (await this._sendLock.UseWaitAsync()) {
                var serializedData = JsonConvert.SerializeObject(message);
                await this.BroadcastAsync(serializedData);
            }
        });
    }

    private const int PrefetchChunk = 16;

    /// <summary>
    /// Warm a deck's cache with keys it hasn't asked for yet (every XIVDeck key in every Stream Deck profile/page,
    /// from <see cref="DeckProfileScanner"/>), so the first visit to any page paints from cache. Entries are
    /// remembered like a normal subscribe (login re-resolution and the macro/gearset change tracking cover them)
    /// and resolved a few per framework tick, each chunk delivered before the next, so it never hitches the game.
    /// A null context warms every connected deck.
    /// </summary>
    internal void Prefetch(string? onlyContextId, IReadOnlyList<(string Type, int Id)> actions) {
        if (actions.Count == 0) return;

        _ = Task.Run(async () => {
            try {
                foreach (var (contextId, session) in this._sessions.ToArray()) {
                    if (!session.WantsPush) continue;
                    if (onlyContextId != null && contextId != onlyContextId) continue;

                    List<(string Type, int Id)> fresh;
                    lock (session.Gate) fresh = actions.Where(action => session.Actions.Add(action)).ToList();
                    if (fresh.Count == 0) continue;

                    var started = System.Diagnostics.Stopwatch.StartNew();
                    var sent = 0;
                    foreach (var chunk in fresh.Chunk(PrefetchChunk)) {
                        if (!this._sessions.ContainsKey(contextId)) break; // deck went away

                        var icons = await Injections.Framework.RunOnFrameworkThread(() => {
                            var resolved = new List<ActionIconDto>(chunk.Length);
                            foreach (var key in chunk) {
                                // macros/gearsets hold character data: leave them for the login refresh
                                if (!Injections.ClientState.IsLoggedIn && ActionIcons.IsPlayerEditable(key.Type)) continue;
                                if (ActionIcons.TryResolve(key.Type, key.Id, out var iconId, out var name))
                                    resolved.Add(new ActionIconDto(key.Type, key.Id, iconId, name));
                            }

                            return resolved.ToArray();
                        });

                        if (icons.Length > 0) await this.DeliverActionsAsync([(contextId, icons)]);
                        sent += icons.Length;
                    }

                    Injections.PluginLog.Debug(
                        $"Prefetched {sent}/{fresh.Count} profile key icon(s) for a Stream Deck in {started.ElapsedMilliseconds}ms " +
                        $"({fresh.Count - sent} unresolved, retried at login)");
                }
            } catch (Exception ex) {
                Injections.PluginLog.Error(ex, "Stream Deck profile prefetch failed");
            }
        });
    }

    /// <summary>
    /// Re-resolve only the player-editable entries (macros, gearsets) each connected deck asked for, and push the
    /// ones whose icon id or name differ from what that deck was last sent. A gearset that was deleted is pushed as
    /// icon 0 so the key clears; one that appears later (or a macro/gearset that couldn't resolve earlier) is pushed
    /// once it resolves. Call from the framework thread; delivery (and any PNG encode) happens off it.
    /// </summary>
    // Player-editable diff is spread across frames: each call resolves keys for at most ~2 ms of main-thread time,
    // then returns true so the watcher calls again next frame until the whole pass is done.
    private const double EditableScanBudgetMs = 2.0;
    private readonly Queue<(string ContextId, (string Type, int Id) Key)> _editableScan = new();
    private readonly Dictionary<string, List<ActionIconDto>> _editableChanged = new();

    internal bool PushEditableChanges() {
        var budget = System.Diagnostics.Stopwatch.StartNew();

        if (this._editableScan.Count == 0) {
            this._editableChanged.Clear();
            foreach (var (contextId, session) in this._sessions) {
                if (!session.WantsPush) continue;
                lock (session.Gate) {
                    foreach (var action in session.Actions)
                        if (ActionIcons.IsPlayerEditable(action.Type)) this._editableScan.Enqueue((contextId, action));
                }
            }
            if (this._editableScan.Count == 0) return false;
        }

        while (this._editableScan.Count > 0 && budget.Elapsed.TotalMilliseconds < EditableScanBudgetMs) {
            var (contextId, key) = this._editableScan.Dequeue();
            if (!this._sessions.TryGetValue(contextId, out var session) || !session.WantsPush) continue;

            var resolved = ActionIcons.TryResolve(key.Type, key.Id, out var iconId, out var name);

            bool hadSent;
            (int IconId, string? Name) last;
            lock (session.Gate) hadSent = session.LastSent.TryGetValue(key, out last);

            ActionIconDto? dto = null;
            if (resolved) {
                if (hadSent && last.IconId == iconId && last.Name == name) continue;
                dto = new ActionIconDto(key.Type, key.Id, iconId, name);
            } else if (hadSent && last.IconId != 0) {
                // e.g. gearset deleted: clear the key rather than keep a stale icon
                dto = new ActionIconDto(key.Type, key.Id, 0);
            }
            if (dto == null) continue;

            if (!this._editableChanged.TryGetValue(contextId, out var list))
                this._editableChanged[contextId] = list = new List<ActionIconDto>();
            list.Add(dto);
        }

        if (this._editableScan.Count > 0) return true;

        var work = new List<(string, ActionIconDto[])>();
        foreach (var (contextId, list) in this._editableChanged) {
            if (list.Count == 0 || !this._sessions.TryGetValue(contextId, out var session)) continue;
            var array = list.ToArray();
            session.NoteSent(array); // so the next pass doesn't queue the same change again
            work.Add((contextId, array));
        }
        this._editableChanged.Clear();

        if (work.Count == 0) return false;

        Injections.PluginLog.Debug(
            $"Player-editable deck entries changed: {string.Join(", ", work.SelectMany(w => w.Item2).Select(a => $"{a.Type}#{a.Id}->{a.IconId}"))}");
        _ = Task.Run(() => this.DeliverActionsAsync(work));
        return false;
    }

    protected override void Dispose(bool disposing) {
        base.Dispose(disposing);

        Injections.PluginLog.Debug("WS server is going away!");
        this._sendLock.Dispose();
    }

    private List<(string ContextId, ActionIconDto[] Actions)> ResolveSubscribed(string? actionType) {
        var work = new List<(string, ActionIconDto[])>();

        foreach (var (contextId, session) in this._sessions) {
            if (!session.WantsPush) continue;

            List<(string Type, int Id)> keys;
            lock (session.Gate) {
                keys = session.Actions.Where(action => actionType == null || action.Type == actionType).ToList();
            }

            if (keys.Count == 0) continue;

            var icons = new List<ActionIconDto>(keys.Count);
            foreach (var key in keys) {
                if (!ActionIcons.TryResolve(key.Type, key.Id, out var iconId, out var name)) continue;
                icons.Add(new ActionIconDto(key.Type, key.Id, iconId, name));
            }

            if (icons.Count > 0)
                work.Add((contextId, icons.ToArray()));
        }

        return work;
    }

    private async Task DeliverHotbarAsync(string? onlyContextId, MicroHotbarSlot[] slots) {
        try {
            if (!this.HasPushTarget(onlyContextId)) return;

            var state = new WSStateUpdateMessage<List<MicroHotbarSlot>>("Hotbar", slots.ToList());
            var statePayload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(state));

            using (await this._sendLock.UseWaitAsync()) {
                foreach (var (context, _) in this.PushTargets(onlyContextId))
                    await this.SendBytes(context, statePayload);
            }

            var iconIds = slots.Select(slot => slot.IconId).Where(id => id > 0).Distinct().ToArray();
            // only encode/send PNGs some target doesn't already have (reconnects report their cached icons)
            var needed = this.NeededBy(this.PushTargets(onlyContextId), iconIds);
            var urls = needed.Length == 0 ? new Dictionary<int, string>() : await IconPngCache.GetDataUrlsAsync(needed);

            var sent = 0;
            using (await this._sendLock.UseWaitAsync()) {
                foreach (var (context, session) in this.PushTargets(onlyContextId))
                    sent += await this.SendMissingIcons(context, session, needed, urls);
            }

            Injections.PluginLog.Debug(
                $"Pushed {slots.Length} hotbar slot(s): {iconIds.Length} icon(s), {sent} PNG(s) sent, rest already on the deck");
        } catch (Exception ex) {
            Injections.PluginLog.Error(ex, "Hotbar push failed");
        }
    }

    private async Task DeliverActionsAsync(IReadOnlyList<(string ContextId, ActionIconDto[] Actions)> work) {
        if (work.Count == 0) return;

        try {
            using (await this._sendLock.UseWaitAsync()) {
                foreach (var (contextId, actions) in work) {
                    var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(
                        new WSStateUpdateMessage<ActionIconDto[]>("ActionIcon", actions)));

                    foreach (var (context, session) in this.PushTargets(contextId)) {
                        session.NoteSent(actions);
                        await this.SendBytes(context, payload);
                    }
                }
            }

            var iconIds = work.SelectMany(item => item.Actions).Select(action => action.IconId).Where(id => id > 0).Distinct().ToArray();
            var missing = work.SelectMany(item => this.NeededBy(this.PushTargets(item.ContextId), iconIds)).Distinct().ToArray();
            var urls = missing.Length == 0 ? new Dictionary<int, string>() : await IconPngCache.GetDataUrlsAsync(missing);

            using (await this._sendLock.UseWaitAsync()) {
                foreach (var (contextId, actions) in work) {
                    var needed = actions.Select(action => action.IconId).Where(id => id > 0).Distinct().ToArray();
                    foreach (var (context, session) in this.PushTargets(contextId))
                        await this.SendMissingIcons(context, session, needed, urls);
                }
            }
        } catch (Exception ex) {
            Injections.PluginLog.Error(ex, "Action icon push failed");
        }
    }

    private bool HasPushTarget(string? onlyContextId) {
        foreach (var (contextId, session) in this._sessions) {
            if (!session.WantsPush) continue;
            if (onlyContextId != null && contextId != onlyContextId) continue;
            return true;
        }

        return false;
    }

    private List<(IWebSocketContext Context, DeckSession Session)> PushTargets(string? onlyContextId) {
        var list = new List<(IWebSocketContext, DeckSession)>();

        foreach (var context in this.ActiveContexts) {
            if (onlyContextId != null && context.Id != onlyContextId) continue;
            if (!this._sessions.TryGetValue(context.Id, out var session) || !session.WantsPush) continue;
            list.Add((context, session));
        }

        return list;
    }

    private int[] NeededBy(List<(IWebSocketContext Context, DeckSession Session)> targets, int[] iconIds) {
        var needed = new HashSet<int>();
        foreach (var (_, session) in targets) {
            lock (session.Gate) {
                foreach (var iconId in iconIds)
                    if (iconId > 0 && !session.SentIcons.Contains(iconId)) needed.Add(iconId);
            }
        }

        return needed.ToArray();
    }

    /// <summary>
    /// The deck reconnected with images already cached: count them as sent so pushes skip those PNGs.
    /// </summary>
    internal void MarkIconsKnown(string contextId, IReadOnlyList<int> iconIds) {
        if (iconIds.Count == 0) return;
        var session = this._sessions.GetOrAdd(contextId, _ => new DeckSession());
        lock (session.Gate) session.SentIcons.UnionWith(iconIds.Where(id => id > 0));
    }

    private async Task<int> SendMissingIcons(
        IWebSocketContext context,
        DeckSession session,
        int[] iconIds,
        IReadOnlyDictionary<int, string> urls) {
        var sent = 0;
        foreach (var iconId in iconIds) {
            if (iconId <= 0) continue;

            lock (session.Gate) {
                if (!session.SentIcons.Add(iconId)) continue;
            }

            if (!urls.TryGetValue(iconId, out var url)) {
                lock (session.Gate) session.SentIcons.Remove(iconId);
                continue;
            }

            var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new WSIconMessage(iconId, url)));
            if (!await this.SendBytes(context, payload)) {
                lock (session.Gate) session.SentIcons.Remove(iconId);
            } else {
                sent++;
            }
        }

        return sent;
    }

    private async Task<bool> SendBytes(IWebSocketContext context, byte[] payload) {
        try {
            if (context.WebSocket.State != WebSocketState.Open) return false;

            await context.WebSocket.SendAsync(payload, true, context.CancellationToken);
            return true;
        } catch (Exception ex) {
            Injections.PluginLog.Debug(ex, "WebSocket send failed");
            return false;
        }
    }
}

internal sealed class DeckSession {
    public readonly object Gate = new();
    public readonly HashSet<(string Type, int Id)> Actions = new();
    public readonly HashSet<int> SentIcons = new();
    public readonly Dictionary<(string Type, int Id), (int IconId, string? Name)> LastSent = new();
    public bool WantsPush;

    public void NoteSent(IEnumerable<ActionIconDto> actions) {
        lock (this.Gate) {
            foreach (var action in actions)
                this.LastSent[(action.Type, action.Id)] = (action.IconId, action.Name);
        }
    }
}
