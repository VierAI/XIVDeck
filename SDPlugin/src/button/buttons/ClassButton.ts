import {BaseButton} from "../BaseButton";
import {KeyDownEvent, WillAppearEvent} from "@rweich/streamdeck-events/dist/Events/Received/Plugin";
import plugin from "../../plugin";
import {FFXIVApi} from "../../link/ffxivplugin/FFXIVApi";
import {IconPushMessage} from "../../link/ffxivplugin/GameTypes";
import {StateCache} from "../../link/ffxivplugin/StateCache";
import {IconLoader} from "../IconLoader";
import {DidReceiveSettingsEvent} from "@rweich/streamdeck-events/dist/Events/Received";

export type ClassButtonSettings = {
    classId: number,
    className: string
}

// class icons are a fixed range; no lookup needed
const classIconId = (classId: number) => 62100 + classId;

export class ClassButton extends BaseButton {
    useGameIcon: boolean = true;
    private readonly icon: IconLoader;
    
    settings?: ClassButtonSettings;

    constructor(event: WillAppearEvent) {
        super(event.context);

        this.icon = new IconLoader(this, {
            known: () => this.settings?.classId == undefined ? undefined : classIconId(this.settings.classId),
            fetchIconId: async () => classIconId(this.settings!.classId),
            remember: () => {}
        });
        
        this._xivEventListeners.add(plugin.xivPluginLink.on("_ready", this.render.bind(this)));
        this._xivEventListeners.add(plugin.xivPluginLink.on("icon", this.onIcon.bind(this)));
        
        this._sdEventListeners.set("keyDown", this.onKeyDown.bind(this));
        
        this.onReceivedSettings(event);
    }
    
    async onReceivedSettings(event: DidReceiveSettingsEvent | WillAppearEvent) {
        this.settings = event.settings as ClassButtonSettings;
        // willAppear: once per connection (pushes keep it fresh, cached keys paint instantly);
        // settings change: always re-ask
        const force = this.isSettingsUpdate();
        this.icon.reset();

        if (this.settings?.classId != undefined) StateCache.requestAction("Class", this.settings.classId, force);

        await this.render();
    }
    
    async onKeyDown(event: KeyDownEvent): Promise<void> {
        if (this.settings?.classId == undefined) {
            throw new Error("No class specified for this button");
        }
        
        await FFXIVApi.GameClass.triggerClass(this.settings.classId);
    }

    override cleanup() {
        this.icon.dispose();
        super.cleanup();
    }
    
    private onIcon(message: IconPushMessage): void {
        if (this.settings?.classId == undefined) return;
        if (message.iconId === classIconId(this.settings.classId)) {
            void this.render();
        }
    }

    async render(): Promise<void> {
        if (!this.useGameIcon) {
            this.setImage("");
            return
        }

        if (!plugin.xivPluginLink.isReady()) {
            return;
        }

        if (this.settings?.classId == undefined) {
            return
        }

        const classId = this.settings.classId;
        StateCache.putAction("Class", classId, classIconId(classId));
        StateCache.requestAction("Class", classId);
        this.icon.paint();
    }
}
