export type LaunchAccount = { steamId: string; accountName: string; expiresAt: string };
export type Mapping = { appId: number; steamId: string; mode: string; enabled: boolean; fixedEnvironment?: string | null; accountName?: string; gameName?: string; launchAccounts?: LaunchAccount[] };
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

export function dualAccounts(mapping: Mapping, identity: Identity, now = Date.now()): LaunchAccount[] {
    const choices = mapping.launchAccounts;
    if (!mapping.enabled || mapping.fixedEnvironment || identity.environment !== 'native' || choices?.length !== 2 ||
        choices.some(a => !Number.isFinite(Date.parse(a.expiresAt)) || Date.parse(a.expiresAt) <= now) ||
        !choices.some(a => a.steamId === identity.steamId)) return [];
    return [...choices].sort((a, b) => Number(b.steamId === identity.steamId) - Number(a.steamId === identity.steamId));
}

export function decodeLaunchOptions(raw: unknown, now = Date.now()): Map<number, LaunchAccount[]> {
    const value = typeof raw === 'string' ? JSON.parse(raw) : raw;
    const result = new Map<number, LaunchAccount[]>();
    if (!value || value.schemaVersion !== 1 || !Array.isArray(value.games) || !Number.isFinite(Date.parse(value.capturedAt)) ||
        now - Date.parse(value.capturedAt) > 90000 || Date.parse(value.capturedAt) > now + 5000) throw new Error('账号许可状态尚未刷新');
    for (const game of value.games) {
        const id = parseAppId(game?.appId);
        if (id === null || result.has(id) || !Array.isArray(game.accounts) || game.accounts.length !== 2 ||
            new Set(game.accounts.map(a => a?.steamId)).size !== 2 || game.accounts.some(a => !a || typeof a.steamId !== 'string' || !/^765\d{14}$/.test(a.steamId) ||
                typeof a.accountName !== 'string' || !a.accountName.trim() || !Number.isFinite(Date.parse(a.expiresAt))))
            throw new Error('账号许可状态格式无效');
        if (game.accounts.every(a => Date.parse(a.expiresAt) > now)) result.set(id, game.accounts);
    }
    return result;
}

export function hookRunGame(
    apps: Record<string, any>,
    mappingFor: (id: number) => Mapping | undefined,
    identity: () => Identity,
    send: (id: number, steamId?: string) => Promise<void>,
    report: (message: string) => void,
): () => void {
    const original = apps?.RunGame;
    if (typeof original !== "function") throw new Error("Steam RunGame 接口不可用，插件没有接管启动。");
    let pending = false;
    const wrapper = function(this: unknown, ...args: unknown[]) {
        const id = parseAppId(args[0]);
        const mapping = id === null ? undefined : mappingFor(id);
        const dual = mapping ? dualAccounts(mapping, identity()) : [];
        if (mapping?.enabled && mapping.launchAccounts?.length === 2 && !dual.length) {
            report('账号或双账号许可状态已变化，请刷新页面后重试。'); return;
        }
        if (id === null || !mapping || !dual.length && !needsRoute(mapping, identity())) return Reflect.apply(original, this, args);
        if (pending) { report("已有游戏启动请求正在提交。"); return; }
        pending = true;
        // Suppress the original call when a routed request fails; otherwise it could run as the wrong user.
        void send(id, dual[0]?.steamId).catch(error => report(String(error))).finally(() => { pending = false; });
    };
    apps.RunGame = wrapper;
    if (apps.RunGame !== wrapper) throw new Error("Steam 启动接口不可写，未能接管按钮。");
    return () => { if (apps.RunGame === wrapper) apps.RunGame = original; };
}
