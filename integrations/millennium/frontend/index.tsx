import { definePlugin, Field, appActionButtonClasses, findModule } from 'millennium';
import { useState, useEffect } from 'react';
import { hookRunGame, Mapping, decodeLaunchOptions } from './routing';
import { decodeConfiguration } from './configuration';
import { installDetailsButtons } from './details';
import { captureLibrary } from './catalog';
import { installVirtualLibrary, VirtualLibraryModel } from './virtual-library';
import { installationIds, uninstalledGames } from './uninstalled';
import { addMissingShortcuts, removeGeneratedShortcuts } from './shortcuts';

declare const backend: {
    fusion_config(): Promise<unknown>;
    fusion_launch(appId: number): Promise<boolean>;
    fusion_launch_as(appId: number, steamId: string): Promise<boolean>;
    fusion_launch_options(): Promise<unknown>;
    fusion_download(appId: number): Promise<boolean>;
    fusion_installations(): Promise<unknown>;
    fusion_signal(steamId: string, loggedOn: boolean): Promise<boolean>;
    fusion_settings(): Promise<boolean>;
    fusion_catalog(steamId: string, payload: string): Promise<boolean>;
};
declare global { interface Window { SteamClient?: any; App?: any; __steamFusionDispose?: () => void; } }

let message = "正在连接 Windows 控制器…";
const observers = new Set<(s: string) => void>();
function report(text: string) { message = text; observers.forEach(fn => fn(text)); console.info('[SteamFusion]', text); }
function currentUser(): string | null {
    try {
        if (window.App?.BHasCurrentUser?.() !== true) return null;
        const id = window.App.GetCurrentUser()?.strSteamID;
        return typeof id === 'string' && /^765\d{14}$/.test(id) ? id : null;
    } catch { return null; }
}
function Settings() {
    const [status, setStatus] = useState(message);
    useEffect(() => { observers.add(setStatus); return () => { observers.delete(setStatus); }; }, []);
    return <div style={{ padding: 16 }}><Field label="账号路由" description={status} />
        <button onClick={() => void backend.fusion_settings().catch(e => report(String(e)))}>打开 SteamFusion 设置</button>
        <p>仅接管已配置且需要换账号或运行环境的游戏。原生按钮适配为实验功能。</p></div>;
}

export default definePlugin(() => {
    window.__steamFusionDispose?.();
    let mappings = new Map<number, Mapping>();
    let environment = "unknown";
    let unhook: (() => void) | undefined;
    let disposed = false;
    let refreshing = false;
    let catalogAt = 0;
    let catalogUser = '';
    let libraryKey = '', libraryDone = '', libraryRunning = false, libraryRetry = 0;
    const send = async (id: number, steamId?: string) => {
        const accepted = steamId ? await backend.fusion_launch_as(id, steamId) : await backend.fusion_launch(id);
        if (!accepted) throw new Error('无法提交启动请求，请打开 SteamFusion 设置检查。');
    };
    let virtualModel: VirtualLibraryModel = { enabled: false, user: null, games: [], loading: true };
    let navigationClass: string | undefined;
    const virtual = installVirtualLibrary({
        document: () => (window as any).g_PopupManager?.GetExistingPopup?.('SP Desktop_uid0')?.m_popup?.window?.document,
        navigationClass: () => navigationClass ??= findModule((m: any) => m.GameListHomeAndSearch && m.CollectionsButton)?.GameListHomeAndSearch,
        model: () => ({ ...virtualModel, enabled: virtualModel.enabled && currentUser() === virtualModel.user }),
        refreshData: () => { void refresh(); },
        download: async id => {
            if (!virtualModel.enabled || currentUser() !== virtualModel.user || !virtualModel.games.some(g => g.appId === id))
                throw new Error('账号或游戏安装状态已变化，请刷新后重试。');
            if (!await backend.fusion_download(id)) throw new Error('下载页面请求未被接受。');
        },
    });
    const details = installDetailsButtons(
        () => (window as any).g_PopupManager?.GetExistingPopup?.('SP Desktop_uid0')?.m_popup?.window?.document,
        id => mappings.get(id), () => ({ steamId: currentUser(), environment }), send, report, () => appActionButtonClasses);
    async function refresh() {
        if (refreshing || disposed) return;
        refreshing = true;
        try {
            const config = decodeConfiguration(await backend.fusion_config());
            if (disposed) return;
            environment = config.environment;
            let choices = new Map<number, import('./routing').LaunchAccount[]>();
            try { choices = decodeLaunchOptions(await backend.fusion_launch_options()); } catch { /* Single-account routing remains available. */ }
            if (disposed) return;
            mappings = new Map((config.games as Mapping[]).map(g => [g.appId, { ...g, launchAccounts: choices.get(g.appId) }]));
            if (!unhook && window.SteamClient?.Apps) {
                unhook = hookRunGame(window.SteamClient.Apps, id => mappings.get(id),
                    () => ({ steamId: currentUser(), environment }),
                    send, report);
                report(`已连接：${mappings.size} 个游戏规则；当前环境 ${environment}。`);
            }
            const user = currentUser();
            const showVirtual = config.library?.uninstalled === true && user === config.library.hostSteamId && environment === 'native';
            if (showVirtual) {
                try {
                    const raw = await backend.fusion_installations();
                    if (disposed || currentUser() !== user) return;
                    const state = typeof raw === 'string' ? JSON.parse(raw) : raw;
                    const installed = installationIds(state);
                    if (config.games.some(g => !state.accounts.includes(g.steamId))) throw new Error('账号列表已变化，正在等待安装状态刷新。');
                    virtualModel = { enabled: true, user, loading: false,
                        games: uninstalledGames(config.games, user, config.library!.hostSteamId, installed, (window as any).appStore?.allApps ?? []) };
                } catch (error) {
                    virtualModel = { enabled: true, user, games: [], loading: true, error: String(error) };
                }
            } else virtualModel = { enabled: false, user, games: [], loading: false };
            virtual.refresh();
            details.refresh();
            await backend.fusion_signal(user ?? "", user !== null);
            libraryKey = JSON.stringify([user, config.library, config.games]);
            if (user && environment === 'native' && config.library && !libraryRunning &&
                libraryDone !== libraryKey && Date.now() >= libraryRetry && (window as any).appStore?.m_bIsInitialized === true) {
                const key = libraryKey;
                libraryRunning = true;
                const active = () => !disposed && libraryKey === key && currentUser() === user;
                const getDetails = async (id: number) => {
                    let timeout: ReturnType<typeof setTimeout> | undefined;
                    try {
                        return await Promise.race([(window as any).appDetailsStore.RequestAppDetails(id),
                            new Promise((_, reject) => { timeout = setTimeout(() => reject(new Error('读取库入口超时')), 6000); })]);
                    } finally { clearTimeout(timeout); }
                };
                const adding = config.library.enabled;
                const sync = adding
                    ? addMissingShortcuts(config.games, (window as any).appStore.allApps, config.library.cliExe,
                        window.SteamClient.Apps, getDetails, active,
                        count => { if (count % 50 === 0) report(`已补充 ${count} 个游戏库入口…`); })
                    : removeGeneratedShortcuts((window as any).appStore.allApps, config.library.cliExe,
                        window.SteamClient.Apps, getDetails, active);
                void sync.then(count => { if (active()) {
                    libraryDone = key;
                    report(adding ? (count ? `已补充 ${count} 个游戏库入口。` : '游戏库入口已检查，无需新增。')
                        : (count ? `已移除 ${count} 个自动生成的入口，保留原生游戏库。` : '使用原生游戏库；新游戏请到所属账号下载。'));
                } })
                    .catch(error => { libraryRetry = Date.now() + 30000; report(`游戏库入口同步失败：${String(error)}`); })
                    .finally(() => { libraryRunning = false; });
            }
            if (user && environment === 'native' && (catalogUser !== user || Date.now() - catalogAt > 60000) &&
                (window as any).appStore?.m_bIsInitialized === true) {
                const snapshot = captureLibrary((window as any).appStore, user, (window as any).appDetailsStore);
                if (currentUser() === user && await backend.fusion_catalog(user, JSON.stringify(snapshot))) {
                    catalogAt = Date.now(); catalogUser = user;
                    console.info('[SteamFusion]', `已采集当前账号游戏库：${snapshot.games.length} 个游戏。`);
                }
            }
        } catch (error) {
            // Keep the last known rules during a temporary backend failure so a mapped
            // launch cannot silently fall through to the wrong account.
            report(`插件连接失败：${String(error)}`);
        } finally { refreshing = false; }
    }
    const timer = setInterval(() => void refresh(), 2000);
    void refresh();
    const dispose = () => {
        disposed = true; clearInterval(timer); unhook?.(); details.dispose(); virtual.dispose();
        if (window.__steamFusionDispose === dispose) delete window.__steamFusionDispose;
    };
    window.__steamFusionDispose = dispose;
    return { title: 'SteamFusion', content: <Settings />, icon: <span>⇄</span>, onDismount: dispose };
});
