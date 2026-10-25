import { FFXIVOpcode } from "../../MessageBase";

export type PluginMode = "Plugin" | "Inspector" | "Developer"

export class InitOpcode extends FFXIVOpcode {
    static override MESSAGE_OPCODE = "init"
    
    version: string;
    mode: PluginMode;
    /** Icon ids whose images this deck already caches (reconnect): the game skips re-sending those PNGs. */
    cachedIcons?: number[];
    
    constructor(version: string, mode: PluginMode, cachedIcons?: number[]) {
        super(InitOpcode.MESSAGE_OPCODE);
        this.version = version;
        this.mode = mode;
        if (cachedIcons != null && cachedIcons.length > 0) this.cachedIcons = cachedIcons;
    }
}
