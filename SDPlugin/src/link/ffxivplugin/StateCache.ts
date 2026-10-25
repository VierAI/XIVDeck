import {FFXIVOpcode} from "./MessageBase";
import {ActionIconMessage, HotbarUpdateMessage, IconPushMessage, StateMessage} from "./GameTypes";
import {SubscribedAction, SubscribedHotbar, SubscribeOpcode} from "./messages/outbound/SubscribeOpcode";

type DeckLink = {
    on(name: string, fn: Function): Function | undefined;
    send(payload: FFXIVOpcode): Promise<unknown>;
    isReady(): boolean;
};

import {FFXIVApi} from "./FFXIVApi";

// don't re-ask the game for the same missing PNG more often than this
const ICON_REASK_MS = 5000;

// coalesce requests from a burst of willAppear events (one page = up to 32 keys, each its own SD message)
// into one subscribe, so the game does one framework hop per page instead of one per key
const FLUSH_DELAY_MS = 15;

/**
 * Fully transparent key image. The game's own "empty" icon (id 0, ui/icon/000000/000000_hr1.tex) is a fully
 * transparent 40x40 tex, which is what upstream XIVDeck 0.4.5 painted for empty hotbar slots; the key shows blank.
 * setImage("") would instead restore the action's default placeholder image.
 */
export const BLANK_IMAGE = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEgAAABICAYAAABV7bNHAAAAK0lEQVR42u3BAQ0AAADCoPdPbQ43oAAAAAAAAAAAAAAAAAAAAAAAAAAAPgxRSAABIBiyrgAAAABJRU5ErkJggg==";

/**
 * Process-lifetime slot and icon cache. Page changes destroy button objects,
 * so this lives next to the websocket, not on the button.
 *
 * Subscriptions are per websocket connection: the game forgets them when the socket drops, so they are cleared on
 * every (re)connect and re-sent by the buttons once the init reply arrives. Requests made before that are held
 * until then (the game ignores push requests from a client it hasn't seen an init from).
 */
class StateCacheImpl {
    usesPush = false;

    private link: DeckLink | null = null;
    private attached = false;
    private initialized = false;
    private _connection = 0;
    private flushScheduled = false;

    private readonly icons = new Map<number, string>();
    private readonly slots = new Map<string, number>();
    private readonly actions = new Map<string, number>();
    private readonly subscribed = new Set<string>();
    private readonly iconAsked = new Map<number, number>();
    private readonly pendingHotbars = new Map<string, SubscribedHotbar>();
    private readonly pendingActions = new Map<string, SubscribedAction>();
    private readonly pendingIcons = new Set<number>();
    private readonly iconFetches = new Map<number, Promise<string>>();

    attach(link: DeckLink): void {
        if (this.attached) return;
        this.attached = true;
        this.link = link;
        link.on("stateUpdate", (data: object) => this.onState(data as StateMessage));
        link.on("icon", (data: object) => this.onIcon(data as IconPushMessage));
        link.on("_wsOpened", () => this.resetConnection());
        link.on("_wsClosed", () => this.resetConnection());
        // registered in the link's constructor, so this runs before any button's _ready handler
        link.on("_ready", () => {
            this.initialized = true;
            this.scheduleFlush();
        });
    }

    /** Bumps on every websocket open/close; timers armed on an older connection are stale. */
    get connection(): number {
        return this._connection;
    }

    /** Icon ids this deck already holds images for; sent with init so the game doesn't re-send them on reconnect. */
    cachedIconIds(): number[] {
        return [...this.icons.keys()];
    }

    /** True once the game answered init on the current connection (API key and push mode are known). */
    get isInitialized(): boolean {
        return this.initialized && this.link != null && this.link.isReady();
    }

    slot(hotbarId: number, slotId: number): number | undefined {
        return this.slots.get(this.slotKey(hotbarId, slotId));
    }

    action(type: string, id: number): number | undefined {
        return this.actions.get(this.actionKey(type, id));
    }

    putSlot(hotbarId: number, slotId: number, iconId: number): void {
        this.slots.set(this.slotKey(hotbarId, slotId), iconId);
    }

    putAction(type: string, id: number, iconId: number): void {
        this.actions.set(this.actionKey(type, id), iconId);
    }

    forgetSlot(hotbarId: number, slotId: number): void {
        this.slots.delete(this.slotKey(hotbarId, slotId));
    }

    forgetAction(type: string, id: number): void {
        this.actions.delete(this.actionKey(type, id));
    }

    putIcon(iconId: number, dataUrl: string): void {
        if (iconId > 0 && dataUrl.length > 0) this.icons.set(iconId, dataUrl);
    }

    /**
     * A data URL paints. "" clears an empty slot. null waits for a push. undefined should HTTP.
     */
    lookup(iconId: number): string | null | undefined {
        if (iconId <= 0) return "";
        const url = this.icons.get(iconId);
        if (url != null) return url;
        if (this.usesPush) return null;
        return undefined;
    }

    /**
     * HTTP fetch of one icon PNG (upstream path), shared by every key that wants it: concurrent callers get the same
     * promise, and the result goes into the cache.
     */
    fetchIcon(iconId: number): Promise<string> {
        const cached = this.icons.get(iconId);
        if (cached != null) return Promise.resolve(cached);

        let pending = this.iconFetches.get(iconId);
        if (pending == null) {
            pending = FFXIVApi.getIcon(iconId)
                .then(data => {
                    this.putIcon(iconId, data);
                    return data;
                })
                .finally(() => this.iconFetches.delete(iconId));
            this.iconFetches.set(iconId, pending);
        }

        return pending;
    }

    /** Drop everything (debug "Redraw All Icons"). Buttons re-request on their next render. */
    clear(): void {
        this.icons.clear();
        this.slots.clear();
        this.actions.clear();
        this.subscribed.clear();
        this.iconAsked.clear();
    }

    /**
     * Ask the game to push this slot's icon id (and PNG). Once per connection unless forced
     * (willAppear / settings change force it).
     */
    requestHotbar(hotbarId: number, slotId: number, force: boolean = false): void {
        const key = this.slotKey(hotbarId, slotId);
        if (!force && this.subscribed.has(key)) return;
        this.subscribed.add(key);
        this.pendingHotbars.set(key, {hotbarId, slotId});
        this.scheduleFlush();
    }

    /**
     * Ask the game to resolve this action's icon id and push it (and its PNG), and to keep pushing changes.
     * This is what non-hotbar keys (actions, emotes, macros, gearsets, classes...) use. Once per connection unless
     * forced (willAppear / settings change force it).
     */
    requestAction(type: string, id: number, force: boolean = false): void {
        const key = this.actionKey(type, id);
        if (!force && this.subscribed.has(key)) return;
        this.subscribed.add(key);
        this.pendingActions.set(key, {type, id});
        this.scheduleFlush();
    }

    /** Ask the game to (re)send a PNG we know the id of but don't have. Throttled per icon. */
    requestIcon(iconId: number): void {
        if (iconId <= 0 || this.icons.has(iconId)) return;
        const now = Date.now();
        const last = this.iconAsked.get(iconId);
        if (last != null && now - last < ICON_REASK_MS) return;
        this.iconAsked.set(iconId, now);
        this.pendingIcons.add(iconId);
        this.scheduleFlush();
    }

    private resetConnection(): void {
        this._connection++;
        this.initialized = false;
        this.subscribed.clear();
        this.iconAsked.clear();
        this.pendingHotbars.clear();
        this.pendingActions.clear();
        this.pendingIcons.clear();
    }

    private onState(message: StateMessage): void {
        if (message.type === "Hotbar") {
            const params = (message as HotbarUpdateMessage).params;
            if (params == null) return;
            for (const slot of params) {
                if (typeof slot.iconId !== "number") continue;
                this.usesPush = true;
                const key = this.slotKey(slot.hotbarId, slot.slotId);
                this.slots.set(key, slot.iconId);
                // the game pushed it, so it's tracked on its side: a key appearing for it needs no subscribe
                this.subscribed.add(key);
            }
            return;
        }

        if (message.type === "ActionIcon") {
            const params = (message as ActionIconMessage).params;
            if (params == null) return;
            this.usesPush = true;
            for (const action of params) {
                const key = this.actionKey(action.type, action.id);
                this.actions.set(key, action.iconId);
                // pushed (subscribe reply, profile prefetch, change push): the game remembers it for this connection
                this.subscribed.add(key);
            }
        }
    }

    private onIcon(message: IconPushMessage): void {
        if (typeof message.iconId !== "number" || typeof message.dataUrl !== "string") return;
        this.usesPush = true;
        this.iconAsked.delete(message.iconId);
        // overwrite: the game re-sends an id when an icon mod changed it
        this.putIcon(message.iconId, message.dataUrl);
    }

    private scheduleFlush(): void {
        if (this.flushScheduled) return;
        this.flushScheduled = true;
        setTimeout(() => {
            this.flushScheduled = false;
            // hold requests until the game has seen our init; _ready flushes them
            if (!this.isInitialized) return;

            const hotbars = [...this.pendingHotbars.values()];
            const actions = [...this.pendingActions.values()];
            const icons = [...this.pendingIcons];
            this.pendingHotbars.clear();
            this.pendingActions.clear();
            this.pendingIcons.clear();
            if (hotbars.length === 0 && actions.length === 0 && icons.length === 0) return;

            // the game caps each list at 128 per message
            for (let i = 0; i < Math.max(hotbars.length, actions.length, icons.length); i += 128) {
                const h = hotbars.slice(i, i + 128);
                const a = actions.slice(i, i + 128);
                const n = icons.slice(i, i + 128);
                this.link!.send(new SubscribeOpcode(h, a, n)).catch(() => this.forget(h, a, n));
            }
            console.debug(`[XIVDeck] subscribe sent: ${hotbars.length} slot(s), ${actions.length} action(s), ${icons.length} icon(s)`);
        }, FLUSH_DELAY_MS);
    }

    private forget(hotbars: SubscribedHotbar[], actions: SubscribedAction[], icons: number[]): void {
        for (const hotbar of hotbars) this.subscribed.delete(this.slotKey(hotbar.hotbarId, hotbar.slotId));
        for (const action of actions) this.subscribed.delete(this.actionKey(action.type, action.id));
        for (const icon of icons) this.iconAsked.delete(icon);
    }

    private slotKey(hotbarId: number, slotId: number): string {
        return `h:${hotbarId}:${slotId}`;
    }

    private actionKey(type: string, id: number): string {
        return `a:${type}:${id}`;
    }
}

export const StateCache = new StateCacheImpl();
