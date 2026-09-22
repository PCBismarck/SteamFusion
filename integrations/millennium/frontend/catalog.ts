import { parseAppId } from './routing.ts';

export type LibraryGame = {
    appId: number; name: string; subscribed: boolean | null; ownerAccountId: number;
    installed: boolean; cloudEnabled: boolean | null;
};
export type LibrarySnapshot = {
    schemaVersion: number; source: string; steamId: string; capturedAt: string; games: LibraryGame[]; includesHiddenGames: boolean;
};

/** Match Steam's own My Games / shared library classification, not LastOwner. */
export function captureLibrary(store: any, steamId: string, details: any, now = new Date()): LibrarySnapshot {
    if (!/^765\d{14}$/.test(steamId) || store?.m_bIsInitialized !== true || !Array.isArray(store.allApps))
        throw new Error('Steam 游戏库尚未完整加载');
    const games: LibraryGame[] = [];
    const seen = new Set<number>();
    for (const app of store.allApps) {
        if (app?.app_type !== 1) continue;
        const appId = parseAppId(app.appid);
        if (appId === null || seen.has(appId) || typeof app.display_name !== 'string' || !app.display_name.trim())
            throw new Error('Steam 游戏库数据格式变化，已停止采集');
        seen.add(appId);
        const owner = app.owner_account_id ?? 0;
        if (!Number.isInteger(owner) || owner < 0 || owner > 0xffffffff) throw new Error('游戏所有者数据无效');
        const detail = details?.GetAppDetails?.(appId);
        games.push({ appId, name: app.display_name.trim(),
            subscribed: typeof app.subscribed_to === 'boolean' ? app.subscribed_to : null,
            ownerAccountId: owner, installed: app.local_per_client_data?.installed === true,
            cloudEnabled: detail?.bCloudAvailable === true && detail?.bCloudEnabledForAccount === true && detail?.bCloudEnabledForApp === true
                ? true : detail?.bCloudAvailable === false || detail?.bCloudEnabledForAccount === false || detail?.bCloudEnabledForApp === false ? false : null });
    }
    return { schemaVersion: 1, source: 'steam-client', steamId, capturedAt: now.toISOString(), games, includesHiddenGames: true };
}
