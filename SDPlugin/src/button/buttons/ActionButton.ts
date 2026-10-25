import { BaseButton } from "../BaseButton";
import { KeyDownEvent, WillAppearEvent } from "@rweich/streamdeck-events/dist/Events/Received/Plugin";
import plugin from "../../plugin";
import { ActionIconMessage, FFXIVAction, IconPushMessage, StateMessage } from "../../link/ffxivplugin/GameTypes";
import { FFXIVApi } from "../../link/ffxivplugin/FFXIVApi";
import { StateCache } from "../../link/ffxivplugin/StateCache";
import { IconLoader } from "../IconLoader";
import { DidReceiveSettingsEvent } from "@rweich/streamdeck-events/dist/Events/Received";

export type ActionButtonSettings = {
    actionType?: string,
    actionId?: number,
    actionName?: string,
    cache?: FFXIVAction
    payload?: unknown
}

export class ActionButton extends BaseButton {
    settings?: ActionButtonSettings;

    useGameIcon: boolean = true;
    private readonly icon: IconLoader;

    constructor(event: WillAppearEvent) {
        super(event.context);

        this.icon = new IconLoader(this, {
            known: () => this.knownIconId(),
            fetchIconId: () => this.fetchIconId(),
            remember: (iconId) => {
                if (this.settings?.actionType != undefined && this.settings.actionId != undefined)
                    StateCache.putAction(this.settings.actionType, this.settings.actionId, iconId);
            }
        });

        this._xivEventListeners.add(plugin.xivPluginLink.on("_ready", this.render.bind(this)));
        this._xivEventListeners.add(plugin.xivPluginLink.on("stateUpdate", this.stateUpdate.bind(this)));
        this._xivEventListeners.add(plugin.xivPluginLink.on("icon", this.onIcon.bind(this)));

        this._sdEventListeners.set("keyDown", this.onKeyDown.bind(this));

        this.onReceivedSettings(event);
    }

    async onReceivedSettings(event: DidReceiveSettingsEvent | WillAppearEvent): Promise<void> {
        this.settings = event.settings as ActionButtonSettings;
        // willAppear: once per connection (pushes keep it fresh, cached keys paint instantly);
        // settings change: always re-ask
        const force = this.isSettingsUpdate();
        this.icon.reset();
        this.migrate();

        // willAppear / settings change: always ask the game for this action's icon, even if asked before
        if (this.settings?.actionType != undefined && this.settings?.actionId != undefined) {
            StateCache.requestAction(this.settings.actionType, this.settings.actionId, force);
        }

        await this.render();
    }

    async onKeyDown(event: KeyDownEvent): Promise<void> {
        if (this.settings?.actionType == undefined || this.settings?.actionId == undefined) {
            throw Error("Not action type/ID was defined for this button!");
        }

        await FFXIVApi.Action.executeAction(this.settings.actionType, this.settings.actionId, this.settings.payload);
    }

    override cleanup() {
        this.icon.dispose();
        super.cleanup();
    }

    private onIcon(message: IconPushMessage): void {
        const iconId = this.knownIconId();
        if (iconId != null && iconId === message.iconId) {
            void this.render();
        }
    }

    private knownIconId(): number | undefined {
        if (this.settings?.actionType == undefined || this.settings.actionId == undefined) return undefined;
        return StateCache.action(this.settings.actionType, this.settings.actionId) ?? this.settings.cache?.iconId;
    }

    private async fetchIconId(): Promise<number> {
        const actionInfo = await FFXIVApi.Action.getAction(this.settings!.actionType!, this.settings!.actionId!);

        if (this.settings != null && this.settings.cache == null) {
            this.settings.cache = actionInfo;
            this.settings.actionName = undefined;
            console.log("MIGRATION PERFORMED!!");
            this.setSettings(this.settings);
        }

        return actionInfo.iconId;
    }

    /**
     * The game pushed a new icon/name for a player-editable entry (gearset renamed or changed job, macro edited).
     * Keep the copy saved in the key's settings current so the property inspector and the pre-connect paint
     * don't show the old one. Only writes when something actually changed.
     */
    private refreshSavedCache(iconId: number, name?: string): void {
        const cache = this.settings?.cache;
        if (this.settings == null || cache == null || iconId <= 0) return;

        const nameChanged = name != null && cache.name !== name;
        if (cache.iconId === iconId && !nameChanged) return;

        cache.iconId = iconId;
        if (nameChanged) cache.name = name!;
        this.setSettings(this.settings);
    }

    // Migration again.
    // TODO: Remove eventually.
    private migrate(): void {
        if (this.settings?.actionType == "Collection") {
            this.settings.actionType = "McGuffin";
            this.setSettings(this.settings);
        }
    }

    async render() {
        if (!this.useGameIcon) {
            this.setImage("");
            return;
        }

        if (!plugin.xivPluginLink.isReady()) {
            return;
        }

        if (this.settings?.actionType == undefined || this.settings?.actionId == undefined) {
            return;
        }

        this.migrate();

        // once per connection (re-sent after every reconnect)
        StateCache.requestAction(this.settings.actionType, this.settings.actionId);
        this.icon.paint();
    }

    private stateUpdate(message: StateMessage) {
        if (message.type === "ActionIcon") {
            const params = (message as ActionIconMessage).params ?? [];
            const mine = params.find(action => action.type === this.settings?.actionType && action.id === this.settings?.actionId);
            if (mine != null) {
                this.refreshSavedCache(mine.iconId, mine.name);
                void this.render();
            }
            return;
        }

        if (this.settings?.actionType == "GearSet" && message.type == "GearSet" && !StateCache.usesPush) {
            // old game plugin (no push): the gearset icon may have changed, look it up again
            if (this.settings.actionId != undefined) StateCache.forgetAction("GearSet", this.settings.actionId);
            void this.render();
        }
    }
}
