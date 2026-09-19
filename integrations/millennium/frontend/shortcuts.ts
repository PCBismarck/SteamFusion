import type { Mapping } from './routing';
import { parseAppId } from './routing.ts';

function canonicalExe(value: unknown): string {
    return typeof value === 'string' ? value.trim().replace(/^"(.*)"$/, '$1').replaceAll('/', '\\').toLowerCase() : '';
}
export function routeInShortcut(detail: any, cliExe: string): number | null {
    if (canonicalExe(detail?.strShortcutExe) !== canonicalExe(cliExe)) return null;
    const match = /^launch ([1-9]\d{0,9})(?: --steamfusion-library)?$/.exec(detail.strShortcutLaunchOptions ?? '');
    return match ? parseAppId(match[1]) : null;
}
function shortcutName(game: Mapping): string {
    const label = game.accountName?.split(' · ')[0]?.trim();
    return `${game.gameName} · ${label || 'SteamFusion'}`;
}

/** Only remove automatically generated entries belonging to this installation. */
export async function removeGeneratedShortcuts(
    visibleApps: any[], cliExe: string, apps: any,
    getDetails: (id: number) => Promise<any>, active: () => boolean,
): Promise<number> {
    let removed = 0;
    for (const app of visibleApps) {
        if (!active()) return removed;
        if (app.app_type !== 1073741824) continue;
        const detail = await getDetails(app.appid);
        if (!detail) throw new Error('无法核对库入口，暂缓清理。');
        if (!active()) return removed;
        if (routeInShortcut(detail, cliExe) === null ||
            !/^launch [1-9]\d{0,9} --steamfusion-library$/.test(detail.strShortcutLaunchOptions ?? '')) continue;
        await apps.RemoveShortcut(app.appid);
        removed++;
        await new Promise(resolve => setTimeout(resolve, 20));
    }
    return removed;
}

/** Steam owns the live VDF write; no concurrent direct file editing or title-based deduplication. */
export async function addMissingShortcuts(
    games: Mapping[], visibleApps: any[], cliExe: string, apps: any,
    getDetails: (id: number) => Promise<any>, active: () => boolean,
    progress: (added: number) => void = () => {},
): Promise<number> {
    const visible = new Set<number>();
    const routed = new Set<number>();
    for (const app of visibleApps) {
        if (!active()) return 0;
        if (app.app_type === 1073741824) {
            const detail = await getDetails(app.appid);
            if (!detail) throw new Error('读取已有库入口失败，暂缓添加以避免重复。');
            const route = routeInShortcut(detail, cliExe);
            if (route !== null) {
                routed.add(route);
                // Repair Steam's executable-derived placeholder; preserve user-renamed entries.
                const game = games.find(g => g.appId === route && g.enabled && g.gameName);
                if (active() && game && detail.strDisplayName === 'SteamFusion.Cli')
                    await apps.SetShortcutName(app.appid, shortcutName(game));
            }
        } else if (app.visible_in_game_list === true) visible.add(app.appid);
    }
    let added = 0;
    const directory = cliExe.slice(0, cliExe.lastIndexOf('\\'));
    for (const game of games) {
        if (!active()) return added;
        if (!game.enabled || !game.gameName || visible.has(game.appId) || routed.has(game.appId)) continue;
        const name = shortcutName(game);
        // Steam's AddShortcut argument layout changes; set each essential field explicitly afterwards.
        const shortcutId = await apps.AddShortcut(name, `"${cliExe}"`, '', '');
        if (!Number.isInteger(shortcutId) || shortcutId < 1 || shortcutId > 0xffffffff)
            throw new Error('Steam 返回的库入口 ID 无效');
        if (!active()) return added;
        try {
            await apps.SetShortcutExe(shortcutId, `"${cliExe}"`);
            await apps.SetShortcutStartDir(shortcutId, `"${directory}"`);
            await apps.SetShortcutLaunchOptions(shortcutId, `launch ${game.appId} --steamfusion-library`);
            await apps.SetShortcutName(shortcutId, name);
            routed.add(game.appId); added++; progress(added);
        } catch (error) {
            if (active()) await apps.RemoveShortcut(shortcutId);
            throw error;
        }
        // Keep Steam responsive while populating large libraries.
        await new Promise(resolve => setTimeout(resolve, 30));
    }
    return added;
}
