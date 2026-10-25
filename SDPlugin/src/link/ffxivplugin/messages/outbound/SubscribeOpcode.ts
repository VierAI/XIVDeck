import {FFXIVOpcode} from "../../MessageBase";

export type SubscribedHotbar = { hotbarId: number, slotId: number };
export type SubscribedAction = { type: string, id: number };

export class SubscribeOpcode extends FFXIVOpcode {
    static override MESSAGE_OPCODE = "subscribe";

    hotbars: SubscribedHotbar[];
    actions: SubscribedAction[];
    /** Icon ids the deck knows but has no image for; the game re-sends these even if it sent them before. */
    icons: number[];

    constructor(hotbars: SubscribedHotbar[], actions: SubscribedAction[], icons: number[] = []) {
        super(SubscribeOpcode.MESSAGE_OPCODE);
        this.hotbars = hotbars;
        this.actions = actions;
        this.icons = icons;
    }
}
