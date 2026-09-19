export type Mapping = { appId: number; steamId: string; mode: string; enabled: boolean; fixedEnvironment?: string | null; accountName?: string; gameName?: string };
export type Identity = { steamId: string | null; environment: string };

/** Large non-Steam GameIDs must never be converted to a lossy JavaScript number. */
export function parseAppId(value: unknown): number | null {
    if (typeof value !== "number" && typeof value !== "string") return null;
    const text = String(value);
    if (!/^[1-9]\d{0,9}$/.test(text)) return null;
    const id = Number(text);
    return Number.isSafeInteger(id) && id <= 0xffffffff ? id : null;
}

export function needsRoute(mapping: Mapping, identity: Identity): boolean {
    return mapping.enabled && (identity.steamId !== mapping.steamId ||
        mapping.mode === "nativeOnly" && identity.environment !== "native" ||
        !!mapping.fixedEnvironment && mapping.fixedEnvironment !== identity.environment);
}

export function hookRunGame(
    apps: Record<string, any>,
    mappingFor: (id: number) => Mapping | undefined,
    identity: () => Identity,
    send: (id: number) => Promise<void>,
    report: (message: string) => void,
): () => void {
    const original = apps?.RunGame;
    if (typeof original !== "function") throw new Error("Steam RunGame 接口不可用，插件没有接管启动。");
    let pending = false;
    const wrapper = function(this: unknown, ...args: unknown[]) {
        const id = parseAppId(args[0]);
        const mapping = id === null ? undefined : mappingFor(id);
        if (id === null || !mapping || !needsRoute(mapping, identity())) return Reflect.apply(original, this, args);
        if (pending) { report("已有游戏启动请求正在提交。"); return; }
        pending = true;
        // Suppress the original call when a routed request fails; otherwise it could run as the wrong user.
        void send(id).catch(error => report(String(error))).finally(() => { pending = false; });
    };
    apps.RunGame = wrapper;
    if (apps.RunGame !== wrapper) throw new Error("Steam 启动接口不可写，未能接管按钮。");
    return () => { if (apps.RunGame === wrapper) apps.RunGame = original; };
}
