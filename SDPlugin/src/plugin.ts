import {FFXIVPluginLink} from "./link/ffxivplugin/FFXIVPluginLink";
import {Streamdeck, Plugin as SDPlugin} from "@rweich/streamdeck-ts";
import {DidReceiveGlobalSettingsEvent, DidReceiveSettingsEvent} from "@rweich/streamdeck-events/dist/Events/Received";
import {
    ApplicationDidLaunchEvent,
    ApplicationDidTerminateEvent, KeyUpEvent, KeyDownEvent,
    DialPressEvent, DialRotateEvent, TouchTapEvent,
    WillAppearEvent, WillDisappearEvent, TitleParametersDidChangeEvent, DialDownEvent, DialUpEvent
} from "@rweich/streamdeck-events/dist/Events/Received/Plugin";
import {DefaultGlobalSettings, GlobalSettings} from "./util/GlobalSettings";
import {ButtonDispatcher} from "./button/ButtonDispatcher";
import {VersionUtils} from "./util/VersionUtils";
import {ObjectUtils} from "./util/ObjectUtils";

class XIVDeckPlugin {
    sdPluginLink: SDPlugin = new Streamdeck().plugin();
    xivPluginLink: FFXIVPluginLink = new FFXIVPluginLink(this.sdPluginLink);

    private dispatcher: ButtonDispatcher = new ButtonDispatcher();

    constructor() {
        this.sdPluginLink.on('didReceiveGlobalSettings', (ev: DidReceiveGlobalSettingsEvent) => this.handleDidReceiveGlobalSettings(ev));

        this.sdPluginLink.on('applicationDidLaunch', (ev: ApplicationDidLaunchEvent) => this.handleApplicationDidLaunch(ev));
        this.sdPluginLink.on('applicationDidTerminate', (ev: ApplicationDidTerminateEvent) => this.handleApplicationDidTerminate(ev));

        // button lifecycle
        this.sdPluginLink.on('willAppear', (ev: WillAppearEvent) => this.dispatcher.handleWillAppear(ev));
        this.sdPluginLink.on('willDisappear', (ev: WillDisappearEvent) => this.dispatcher.handleWillDisappear(ev));
        this.sdPluginLink.on('didReceiveSettings', (ev: DidReceiveSettingsEvent) => this.dispatcher.handleReceivedSettings(ev));

        // button interactivity events
        this.sdPluginLink.on('keyDown', (ev: KeyDownEvent) => this.dispatcher.dispatch(ev));
        this.sdPluginLink.on('keyUp', (ev: KeyUpEvent) => this.dispatcher.dispatch(ev));
        this.sdPluginLink.on('dialRotate', (ev: DialRotateEvent) => this.dispatcher.dispatch(ev));
        this.sdPluginLink.on('dialDown', (ev: DialDownEvent) => this.dispatcher.dispatch(ev));
        this.sdPluginLink.on('dialUp', (ev: DialUpEvent) => this.dispatcher.dispatch(ev));
        this.sdPluginLink.on('touchTap', (ev: TouchTapEvent) => this.dispatcher.dispatch(ev));
        this.sdPluginLink.on('titleParametersDidChange', (ev: TitleParametersDidChangeEvent) => this.dispatcher.dispatch(ev));

        if (typeof process === "undefined" || process.platform === "linux") {
            this.xivPluginLink.connect(true);
        }

        // deprecated events
        this.sdPluginLink.on('dialPress', (ev: DialPressEvent) => {
            let appInfo = this.sdPluginLink.info.application as Record<string, string>;
            if (VersionUtils.semverCompare(appInfo['version'], '6.1') >= 0) {
                console.debug('Got deprecated event dialPress in a version that sends new events, ignoring.');
                return;
            }

            this.dispatcher.dispatch(ev);
        });

    }

    handleDidReceiveGlobalSettings(event: DidReceiveGlobalSettingsEvent) {
        this.sdPluginLink.logMessage(`Received global settings: ${JSON.stringify(event.settings)}`);

        let globalSettings = ObjectUtils.deepMerge(DefaultGlobalSettings, (event.settings as GlobalSettings) || {});
        const hostname = globalSettings.ws.hostname || this.xivPluginLink.hostname;
        const port = globalSettings.ws.port;
        const changed = hostname !== this.xivPluginLink.hostname || port !== this.xivPluginLink.port;
        this.xivPluginLink.hostname = hostname;
        this.xivPluginLink.port = port;

        this.sdPluginLink.logMessage(`FINAL PAYLOAD: ${JSON.stringify(globalSettings)}`);

        if (this.xivPluginLink.hasSocket()) {
            // Stream Deck sends global settings on every game-launch notification (and plugin start). Only drop the
            // connection if the target actually changed; reconnecting for nothing made the game re-push everything.
            if (changed) {
                this.xivPluginLink.gracefulClose(); // will automatically reconnect, so long as the game is still alive.
            } else {
                this.sdPluginLink.logMessage("Websocket target unchanged; keeping the current connection.");
            }
        } else {
            this.xivPluginLink.connect(true);
        }
    }

    handleApplicationDidLaunch(ev: ApplicationDidLaunchEvent) {
        this.sdPluginLink.logMessage(`Received launch notification for process: ${ev.application}`);

        // on launch, mark the game as alive and request global settings to force connect.
        this.xivPluginLink.isGameAlive = true;
        this.sdPluginLink.getGlobalSettings(this.sdPluginLink.pluginUUID!);
    }

    handleApplicationDidTerminate(ev: ApplicationDidTerminateEvent) {
        this.sdPluginLink.logMessage(`Received shutdown notification for process: ${ev.application}`);

        // Mark the game as dead, and stop retries.
        this.xivPluginLink.isGameAlive = false;
        this.xivPluginLink.shutdown();
    }
}

const plugin = new XIVDeckPlugin();

(<any>window).sdPlugin = plugin;
export default plugin;
