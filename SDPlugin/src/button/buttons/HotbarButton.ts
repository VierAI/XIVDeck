import {BaseButton} from "../BaseButton";
import {KeyDownEvent, WillAppearEvent} from "@rweich/streamdeck-events/dist/Events/Received/Plugin";
import plugin from "../../plugin";
import {FFXIVApi} from "../../link/ffxivplugin/FFXIVApi";
import {HotbarUpdateMessage, IconPushMessage, StateMessage} from "../../link/ffxivplugin/GameTypes";
import {BLANK_IMAGE, StateCache} from "../../link/ffxivplugin/StateCache";
import {IconLoader} from "../IconLoader";
import {DidReceiveSettingsEvent} from "@rweich/streamdeck-events/dist/Events/Received";

export type HotbarButtonSettings = {
    hotbarId: number,
    slotId: number
}

export class HotbarButton extends BaseButton {

    settings?: HotbarButtonSettings;
    private readonly icon: IconLoader;

    constructor(event: WillAppearEvent) {
        super(event.context);

        this.icon = new IconLoader(this, {
            known: () => this.settings?.hotbarId == undefined || this.settings.slotId == undefined
                ? undefined
                : StateCache.slot(this.settings.hotbarId, this.settings.slotId),
            fetchIconId: async () => (await FFXIVApi.Hotbar.getHotbarSlot(this.settings!.hotbarId, this.settings!.slotId)).iconId,
            remember: (iconId) => {
                if (this.settings?.hotbarId != undefined && this.settings.slotId != undefined)
                    StateCache.putSlot(this.settings.hotbarId, this.settings.slotId, iconId);
            }
        }, BLANK_IMAGE); // empty slot (icon 0) paints blank, like the game and upstream 0.4.5

        this._xivEventListeners.add(plugin.xivPluginLink.on("_ready", this.render.bind(this)));
        this._xivEventListeners.add(plugin.xivPluginLink.on("stateUpdate", this.stateUpdate.bind(this)));
        this._xivEventListeners.add(plugin.xivPluginLink.on("icon", this.onIcon.bind(this)));

        this._sdEventListeners.set("keyDown", this.onKeyDown.bind(this));

        this.onReceivedSettings(event);
    }

    async onReceivedSettings(event: DidReceiveSettingsEvent | WillAppearEvent) {
        this.settings = event.settings as HotbarButtonSettings;
        // willAppear: once per connection (pushes keep it fresh, cached keys paint instantly);
        // settings change: always re-ask
        const force = this.isSettingsUpdate();
        this.icon.reset();

        if (this.settings?.hotbarId != undefined && this.settings?.slotId != undefined)
            StateCache.requestHotbar(this.settings.hotbarId, this.settings.slotId, force);

        await this.render();
    }

    async onKeyDown(event: KeyDownEvent): Promise<void> {
        if (this.settings?.hotbarId == undefined || this.settings?.slotId == undefined) {
            throw new Error("No hotbarId/slotId defined for this button");
        }

        await FFXIVApi.Hotbar.triggerHotbarSlot(this.settings.hotbarId, this.settings.slotId);
    }

    async stateUpdate(message: StateMessage) : Promise<void> {
        if (message.type != "Hotbar") return;
        let hotbarMessage = message as HotbarUpdateMessage;

        if (this.settings == null) return;

        if (hotbarMessage.params == null) {
            console.debug("Got update without specifying slots, updating!")
            StateCache.forgetSlot(this.settings.hotbarId, this.settings.slotId);
            await this.render();
            return;
        }

        for (let slot of hotbarMessage.params) {
            if (slot.hotbarId == this.settings.hotbarId && slot.slotId == this.settings.slotId) {
                console.debug("got opportunistic update...", slot, this.settings)
                // old game plugin sends no iconId: drop the cached id so render looks it up again
                if (typeof slot.iconId !== "number") StateCache.forgetSlot(slot.hotbarId, slot.slotId);
                await this.render();
                return;
            }
        }
    }

    private onIcon(message: IconPushMessage): void {
        if (this.settings?.hotbarId == undefined || this.settings.slotId == undefined) return;
        const iconId = StateCache.slot(this.settings.hotbarId, this.settings.slotId);
        if (iconId != null && iconId === message.iconId) {
            void this.render();
        }
    }

    override cleanup() {
        this.icon.dispose();
        super.cleanup();
    }

    async render() : Promise<void> {
        if (!plugin.xivPluginLink.isReady()) {
            return;
        }

        if (this.settings?.hotbarId == undefined || this.settings?.slotId == undefined) {
            return
        }

        // once per connection; the game also pushes every slot on connect and on change
        StateCache.requestHotbar(this.settings.hotbarId, this.settings.slotId);
        this.icon.paint();
    }
}
