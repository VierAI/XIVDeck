import {BaseButton} from "../BaseButton";
import {KeyDownEvent, WillAppearEvent} from "@rweich/streamdeck-events/dist/Events/Received/Plugin";
import plugin from "../../plugin";
import {FFXIVApi} from "../../link/ffxivplugin/FFXIVApi";
import {ActionIconMessage, IconPushMessage, StateMessage} from "../../link/ffxivplugin/GameTypes";
import {StateCache} from "../../link/ffxivplugin/StateCache";
import {IconLoader} from "../IconLoader";
import {DidReceiveSettingsEvent} from "@rweich/streamdeck-events/dist/Events/Received";

export type MacroButtonSettings = { 
    macroId: number
}

export class MacroButton extends BaseButton {
    
    settings?: MacroButtonSettings;
    useGameIcon: boolean = true;
    private readonly icon: IconLoader;

    constructor(event: WillAppearEvent) {
        super(event.context);

        this.icon = new IconLoader(this, {
            known: () => this.settings?.macroId == undefined ? undefined : StateCache.action("Macro", this.settings.macroId),
            fetchIconId: async () => (await FFXIVApi.Action.getAction("Macro", this.settings!.macroId)).iconId,
            remember: (iconId) => {
                if (this.settings?.macroId != undefined) StateCache.putAction("Macro", this.settings.macroId, iconId);
            }
        });

        this._xivEventListeners.add(plugin.xivPluginLink.on("_ready", this.render.bind(this)));
        this._xivEventListeners.add(plugin.xivPluginLink.on("stateUpdate", this.stateUpdate.bind(this)));
        this._xivEventListeners.add(plugin.xivPluginLink.on("icon", this.onIcon.bind(this)));
        
        this._sdEventListeners.set("keyDown", this.onKeyDown.bind(this));
        
        this.onReceivedSettings(event);
    }
    
    async onReceivedSettings(event: DidReceiveSettingsEvent | WillAppearEvent) {
        this.settings = event.settings as MacroButtonSettings;
        // willAppear: once per connection (pushes keep it fresh, cached keys paint instantly);
        // settings change: always re-ask
        const force = this.isSettingsUpdate();
        this.icon.reset();

        // willAppear / settings change: always ask the game for this macro's icon
        if (this.settings?.macroId != undefined) StateCache.requestAction("Macro", this.settings.macroId, force);

        await this.render();
    }

    async onKeyDown(event: KeyDownEvent): Promise<void> {
        if (this.settings?.macroId == undefined) {
            throw Error("No macro ID was defined for this button!");
        }
        
        await FFXIVApi.Action.executeAction("Macro", this.settings.macroId);
    }

    override cleanup() {
        this.icon.dispose();
        super.cleanup();
    }

    private onIcon(message: IconPushMessage): void {
        if (this.settings?.macroId == undefined) return;
        const iconId = StateCache.action("Macro", this.settings.macroId);
        if (iconId != null && iconId === message.iconId) {
            void this.render();
        }
    }

    async stateUpdate(message: StateMessage) : Promise<void> {
        if (message.type === "ActionIcon") {
            const params = (message as ActionIconMessage).params ?? [];
            if (params.some(action => action.type === "Macro" && action.id === this.settings?.macroId)) {
                await this.render();
            }
            return;
        }

        if (message.type != "Macro" || StateCache.usesPush) return;
        // old game plugin (no push): the macro icon may have changed, look it up again
        if (this.settings?.macroId != undefined) StateCache.forgetAction("Macro", this.settings.macroId);
        await this.render();
    }

    async render() : Promise<void> {
        if (!this.useGameIcon) {
            this.setImage("");
            return
        }
        
        if (!plugin.xivPluginLink.isReady() || this.settings?.macroId == undefined) {
            return;
        }

        // once per connection (re-sent after every reconnect)
        StateCache.requestAction("Macro", this.settings.macroId);
        this.icon.paint();
    }
}
