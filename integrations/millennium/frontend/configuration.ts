import type { Mapping } from './routing';

export type PluginConfiguration = { environment: string; games: Mapping[]; library?: { enabled: boolean; cliExe: string; uninstalled?: boolean; hostSteamId?: string | null } };

/** Millennium may decode a Lua JSON string before returning it through FFI. */
export function decodeConfiguration(value: unknown): PluginConfiguration {
    const config = typeof value === 'string' ? JSON.parse(value) : value;
    if (!config || typeof config !== 'object' || typeof config.environment !== 'string' ||
        !/^[A-Za-z][A-Za-z0-9_]{0,31}$/.test(config.environment) || !Array.isArray(config.games)) {
        throw new Error('SteamFusion 配置格式无效');
    }
    const ids = new Set<number>();
    if (config.library != null && (typeof config.library.enabled !== 'boolean' || typeof config.library.cliExe !== 'string' ||
        !/^[A-Za-z]:\\[^"\r\n]+\\SteamFusion\.Cli\.exe$/i.test(config.library.cliExe))) throw new Error('游戏库入口配置无效');
    if (config.library?.uninstalled != null && typeof config.library.uninstalled !== 'boolean' ||
        config.library?.hostSteamId != null && !/^765\d{14}$/.test(config.library.hostSteamId)) throw new Error('未安装游戏页配置无效');
    for (const game of config.games) {
        if (!game || !Number.isInteger(game.appId) || game.appId < 1 || game.appId > 0xffffffff ||
            ids.has(game.appId) || typeof game.steamId !== 'string' || !/^765\d{14}$/.test(game.steamId) ||
            !['auto', 'nativeOnly'].includes(game.mode) || typeof game.enabled !== 'boolean' ||
            (game.accountName != null && typeof game.accountName !== 'string') ||
            (game.gameName != null && (typeof game.gameName !== 'string' || !game.gameName.trim())) ||
            (game.fixedEnvironment != null && (typeof game.fixedEnvironment !== 'string' ||
                !/^[A-Za-z][A-Za-z0-9_]{0,31}$/.test(game.fixedEnvironment)))) {
            throw new Error('SteamFusion 游戏路由格式无效');
        }
        ids.add(game.appId);
    }
    return config;
}
