import {BaseButton} from "./BaseButton";
import {StateCache} from "../link/ffxivplugin/StateCache";

// the game sends slot/action state first and the PNGs right after; give those time to land before asking again
const PUSH_GRACE_MS = 1500;
// after asking the game to re-send a missing PNG, wait this long before the upstream HTTP fetch
const RESEND_GRACE_MS = 2500;

export type IconTarget = {
    /** Icon id the cache (or saved settings) says this key shows, or undefined if not known yet. */
    known(): number | undefined;
    /** Upstream lookup of the icon id over HTTP. */
    fetchIconId(): Promise<number>;
    /** Remember an icon id learned over HTTP so later renders paint from cache. */
    remember(iconId: number): void;
};

/**
 * Paints a key from the shared StateCache. If the image isn't cached yet it waits for the game's push; only if that
 * doesn't arrive does it ask the game to re-send it, and only if that fails too does it fall back to the upstream
 * HTTP lookup. Timers belong to one websocket connection and one key appearance: a reconnect or the key scrolling
 * away makes them no-ops, and a push that satisfies the key cancels them.
 */
export class IconLoader {
    private timer?: ReturnType<typeof setTimeout>;
    private httpInFlight = false;
    private generation = 0;
    private disposed = false;

    /**
     * @param emptyImage what to paint for icon id 0 ("nothing here"): "" restores the key's default image,
     *                   BLANK_IMAGE paints it blank like an empty in-game slot.
     */
    constructor(
        private readonly button: BaseButton,
        private readonly target: IconTarget,
        private readonly emptyImage: string = ""
    ) {}

    /** The key disappeared (page flip): stop timers and drop anything still in flight for it. */
    dispose(): void {
        this.disposed = true;
        this.reset();
    }

    /** The key now points at something else: drop any pending fallback and ignore in-flight results. */
    reset(): void {
        this.cancel();
        this.generation++;
    }

    cancel(): void {
        if (this.timer != null) clearTimeout(this.timer);
        this.timer = undefined;
    }

    paint(): void {
        if (this.disposed || !StateCache.isInitialized) return;

        const known = this.target.known();
        if (known != null) {
            const data = StateCache.lookup(known);
            if (typeof data === "string") {
                this.cancel();
                // "" is the cached "empty" state (icon 0), not a miss
                this.button.setImage(data === "" ? this.emptyImage : data);
                return;
            }

            if (data === null) {
                // most likely the PNG is right behind the state message: wait, don't ask yet
                this.arm(PUSH_GRACE_MS, () => this.onPushMissed());
                return;
            }
        } else if (StateCache.usesPush) {
            this.arm(PUSH_GRACE_MS, () => this.onPushMissed());
            return;
        }

        void this.http("no push support");
    }

    private onPushMissed(): void {
        const known = this.target.known();
        if (known != null) {
            if (typeof StateCache.lookup(known) === "string") {
                this.paint();
                return;
            }

            console.debug(`[XIVDeck] icon ${known} not pushed within ${PUSH_GRACE_MS}ms; asking the game to re-send it`);
            StateCache.requestIcon(known);
            this.arm(RESEND_GRACE_MS, () => {
                const now = this.target.known();
                if (now != null && typeof StateCache.lookup(now) === "string") this.paint();
                else void this.http(`icon ${known} still missing after re-send request`);
            });
            return;
        }

        void this.http(`target not pushed within ${PUSH_GRACE_MS}ms`);
    }

    private arm(delayMs: number, then: () => void): void {
        if (this.timer != null) return;
        const connection = StateCache.connection;
        const generation = this.generation;
        this.timer = setTimeout(() => {
            this.timer = undefined;
            // key gone, retargeted, or the socket reconnected since: the new connection's render re-arms if needed
            if (this.disposed || generation !== this.generation) return;
            if (connection !== StateCache.connection || !StateCache.isInitialized) return;
            then();
        }, delayMs);
    }

    private async http(reason: string): Promise<void> {
        if (this.disposed || this.httpInFlight || !StateCache.isInitialized) return;
        this.httpInFlight = true;
        const generation = this.generation;

        try {
            console.debug(`[XIVDeck] HTTP fallback for ${this.button.context}: ${reason}`);
            const iconId = await this.target.fetchIconId();
            if (this.disposed || generation !== this.generation) return;

            // a push may have landed while we waited
            const now = this.target.known();
            if (now != null && now !== iconId && typeof StateCache.lookup(now) === "string") {
                this.paint();
                return;
            }

            this.target.remember(iconId);
            if (iconId <= 0) {
                this.cancel();
                this.button.setImage(this.emptyImage);
                return;
            }

            const data = await StateCache.fetchIcon(iconId);
            if (this.disposed || generation !== this.generation) return;

            this.cancel();
            this.button.setImage(data);
        } catch (e) {
            // e.g. macro/gearset before login: the game pushes it when it can resolve it
            console.warn("[XIVDeck] Icon lookup failed; waiting for the game to push it", e);
        } finally {
            this.httpInFlight = false;
        }
    }
}
