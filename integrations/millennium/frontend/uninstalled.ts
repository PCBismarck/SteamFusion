import type { Mapping } from './routing';
import { parseAppId } from './routing.ts';

export const librarySorts = [
    { value: 'name-asc', label: '名称 · 升序' },
    { value: 'name-desc', label: '名称 · 降序' },
    { value: 'alphabetical-asc', label: '字母 · A–Z' },
    { value: 'alphabetical-desc', label: '字母 · Z–A' },
    { value: 'appid-asc', label: 'AppID · 从小到大' },
    { value: 'appid-desc', label: 'AppID · 从大到小' },
] as const;
export type LibrarySort = typeof librarySorts[number]['value'];
export function librarySort(value: unknown): LibrarySort {
    return librarySorts.some(option => option.value === value) ? value as LibrarySort : 'name-asc';
}
const names = new Intl.Collator('zh-CN');
const letters = new Intl.Collator('en', { sensitivity: 'base', numeric: false });
/** Sort a copy of the complete result set before limiting the rendered cards. */
export function sortGames(games: Mapping[], order: LibrarySort): Mapping[] {
    return [...games].sort((a, b) => {
        if (order === 'appid-asc') return a.appId - b.appId;
        if (order === 'appid-desc') return b.appId - a.appId;
        if (order === 'alphabetical-asc' || order === 'alphabetical-desc') {
            const byLetter = letters.compare(a.gameName ?? '', b.gameName ?? '');
            return (order === 'alphabetical-desc' ? -byLetter : byLetter) || a.appId - b.appId;
        }
        const byName = names.compare(a.gameName ?? '', b.gameName ?? '');
        return (order === 'name-desc' ? -byName : byName) || a.appId - b.appId;
    });
}

export function installationIds(value: unknown, now = Date.now()): Set<number> {
    const state = typeof value === 'string' ? JSON.parse(value) : value;
    if (!state || state.schemaVersion !== 1 || state.complete !== true || !Array.isArray(state.installed) ||
        !Array.isArray(state.accounts) || state.accounts.some((id: unknown) => typeof id !== 'string' || !/^765\d{14}$/.test(id)) ||
        !Number.isFinite(Date.parse(state.capturedAt)) || now - Date.parse(state.capturedAt) > 90000 || Date.parse(state.capturedAt) > now + 5000 ||
        state.installed.some((id: unknown) => typeof id !== 'number' || parseAppId(id) === null))
        throw new Error('正在等待最新安装清单，暂缓未安装游戏页更新。');
    return new Set(state.installed);
}

/** A virtual view only; never adds, removes or classifies native Steam entries. */
export function uninstalledGames(games: Mapping[], viewer: string | null, host: string | null | undefined, installed: Set<number>, visible: any[]): Mapping[] {
    if (!viewer || viewer !== host) return [];
    const local = new Set(installed);
    for (const app of visible) if (app.app_type !== 1073741824 &&
        (app.local_per_client_data?.installed === true || app.subscribed_to === true)) local.add(app.appid);
    return sortGames(games.filter(game => game.enabled && game.steamId !== viewer && game.gameName && !local.has(game.appId)), 'name-asc');
}
export function filterGames(games: Mapping[], query: string): Mapping[] {
    const search = query.trim().toLocaleLowerCase();
    return games.filter(game => !search || game.gameName!.toLocaleLowerCase().includes(search) || String(game.appId).includes(search));
}
export function artworkUrl(appId: number, kind: 'cover' | 'hero' | 'header'): string {
    if (parseAppId(appId) === null) throw new Error('AppID 无效');
    const file = { cover: 'library_600x900.jpg', hero: 'library_hero.jpg', header: 'header.jpg' }[kind];
    return `https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/${appId}/${file}`;
}
