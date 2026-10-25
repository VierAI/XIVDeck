import {DidReceiveSettingsEvent} from "@rweich/streamdeck-events/dist/Events/Received";
import {WillAppearEvent} from "@rweich/streamdeck-events/dist/Events/Received/Plugin";
import {
    GenericLayoutFeedback,
    LayoutFeedback, 
    LayoutFeedbackKey
} from "@rweich/streamdeck-events/dist/StreamdeckTypes/Received/Feedback/LayoutFeedback";

import plugin from "../plugin";
import { InteractiveEvent } from "../util/SDEvent";

type SetterTargets = 'hardware' | 'software' | 'both';

export abstract class BaseButton {
    public context: string;
    
    // set of registered XIV events bound to this button
    protected _xivEventListeners: Set<Function | undefined> = new Set<Function | undefined>();
    
    // set of event listeners
    protected _sdEventListeners: Map<InteractiveEvent['event'], Function> = new Map<InteractiveEvent['event'], Function>();

    // last image sent for this key, to skip redundant setImage calls (each one is a full image upload to the
    // Stream Deck app, and they queue behind each other when flipping pages quickly)
    private _lastImage?: string;
    private _settingsSeen = false;

    protected constructor(context: string) {
        this.context = context;
    }

    /**
     * False for the settings that arrive with willAppear (the key just came on screen), true for a later
     * didReceiveSettings (the user changed the key). Call once per onReceivedSettings.
     */
    protected isSettingsUpdate(): boolean {
        const update = this._settingsSeen;
        this._settingsSeen = true;
        return update;
    }
    
    abstract onReceivedSettings(event: DidReceiveSettingsEvent | WillAppearEvent): Promise<void>;
    abstract render(): Promise<void>;
    
    // cleanup tasks, if any, can be specified by overriding this particular method
    public cleanup() {
        this._xivEventListeners.forEach((eventDeleter) => {
            if (eventDeleter) eventDeleter();
        });
        
        this._sdEventListeners.clear();
    }
    
    public dispatch(event: InteractiveEvent): Promise<void> {
        let eventHandler = this._sdEventListeners.get(event.event);
        if (eventHandler) {
            return eventHandler(event);
        }
        
        // no event handler exists for this, just suppress
        return Promise.resolve();
    }
    
    /* wrappers for the exposed elgato api, except with built-in context sensitivity */
    
    public showAlert(): void {
        plugin.sdPluginLink.showAlert(this.context);
    }
    
    public showOk(): void {
        plugin.sdPluginLink.showOk(this.context);
    }
    
    public setSettings(settings: unknown) {
        plugin.sdPluginLink.setSettings(this.context, settings);
    }
    
    public setImage(imageData: string, options: { target?: SetterTargets; state?: number }= {}): void {
        const plain = options.target == null && options.state == null;
        if (plain && imageData === this._lastImage) return;
        this._lastImage = plain ? imageData : undefined;
        plugin.sdPluginLink.setImage(imageData, this.context, options);
    }
    
    public setTitle(title: string, options: { target?: SetterTargets; state?: number } = {}): void {
        plugin.sdPluginLink.setTitle(title, this.context, options);
    }
    
    public setState(state: number) {
        plugin.sdPluginLink.setState(state, this.context);
    }
    
    public setFeedback(payload: LayoutFeedback | GenericLayoutFeedback) {
        plugin.sdPluginLink.setFeedback(payload, this.context);
    }
    
    public setFeedbackLayout(layout: LayoutFeedbackKey | string) {
        plugin.sdPluginLink.setFeedbackLayout(layout, this.context);
    }
}
