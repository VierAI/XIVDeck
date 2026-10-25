using XIVDeck.FFXIVPlugin.Game.Chat;
using XIVDeck.FFXIVPlugin.Server.Messages.Outbound;

namespace XIVDeck.FFXIVPlugin.Game; 

public static class DalamudHooks {
    public static void OnGameLogin() {
        DeferredChat.SendDeferredMessages(6000);
        
        // this should probably be moved, but update macro/gearset states at login
        XIVDeckPlugin.Instance.Server.BroadcastMessage(new WSStateUpdateMessage("GearSet"));
        XIVDeckPlugin.Instance.Server.BroadcastMessage(new WSStateUpdateMessage("Macro"));
        XIVDeckPlugin.Instance.HotbarWatcher.PublishAll();
        // every remembered Stream Deck action, not just gearsets/macros: anything that failed to resolve at the
        // title screen gets its icon now
        XIVDeckPlugin.Instance.Server.RefreshSubscribedActions(null);
    }
}