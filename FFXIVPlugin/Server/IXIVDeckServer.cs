using System;
using System.Collections.Generic;
using XIVDeck.FFXIVPlugin.Server.Messages;
using XIVDeck.FFXIVPlugin.Server.Types;

namespace XIVDeck.FFXIVPlugin.Server; 

public interface IXIVDeckServer : IDisposable {
    public void StartServer();
    public void StopServer();
    
    public bool IsRunning { get; }

    /// <summary>
    /// Send a message to the bidirectional channel of all clients connected to this server.
    /// </summary>
    /// <remarks>
    /// This method may be called synchronously; it will spawn a task to handle async message delivery.
    /// </remarks>
    /// <param name="message">The base message to send to a connected client.</param>
    public void BroadcastMessage(BaseOutboundMessage message);

    public void NoteMode(string contextId, bool wantsPush);

    public void RememberActions(string contextId, IReadOnlyList<(string Type, int Id)> actions);

    /// <summary>True when at least one Stream Deck websocket client is connected. Cheap; safe to call every frame.</summary>
    public bool HasClients { get; }

    public void BroadcastHotbar(IReadOnlyList<MicroHotbarSlot> slots);

    public void SendHotbar(string contextId, IReadOnlyList<MicroHotbarSlot> slots);

    public void SendActionIcons(string contextId, IReadOnlyList<ActionIconDto> actions);

    public void RefreshSubscribedActions(string? actionType);

    public void SendIcons(string contextId, IReadOnlyList<int> iconIds);

    public void ForgetSentIcons();

    public void ReplaceIcons(IReadOnlyList<int> iconIds);

    /// <returns>true while a frame-budgeted pass is still in progress</returns>
    public bool PushEditableChanges();

    public void MarkIconsKnown(string contextId, IReadOnlyList<int> iconIds);

    public void Prefetch(string? contextId, IReadOnlyList<(string Type, int Id)> actions);
}