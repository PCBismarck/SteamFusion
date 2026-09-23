import { needsRoute, parseAppId, dualAccounts } from './routing.ts';
import type { Identity, Mapping } from './routing.ts';

/** Read the committed Steam component, including React's alternate after navigation. */
export function appIdFromFiber(fiber: any): number | null {
    if (!fiber) return null;
    let root = fiber;
    const seen = new Set();
    while (root.return && !seen.has(root)) { seen.add(root); root = root.return; }
    if (root.return || !root.stateNode?.current) return null;
    if (root.stateNode.current !== root) {
        if (root.stateNode.current !== root.alternate || !fiber.alternate) return null;
        fiber = fiber.alternate;
    }
    for (let n = 0; fiber && n < 64; n++, fiber = fiber.return) {
        const props = fiber.memoizedProps;
        const value = props?.overview?.appid ?? props?.appid;
        if (value !== undefined) return parseAppId(value);
    }
    return null;
}

function appIdFor(element: HTMLElement): number | null {
    const key = Object.keys(element).find(k => k.startsWith('__reactFiber$'));
    return key ? appIdFromFiber((element as any)[key]) : null;
}

export function installDetailsButtons(
    getDocument: () => Document | undefined,
    mappingFor: (id: number) => Mapping | undefined,
    identity: () => Identity,
    send: (id: number, steamId?: string) => Promise<void>,
    report: (text: string) => void,
    nativeClasses: () => Record<string, string> | undefined,
): { refresh: () => void; dispose: () => void } {
    let doc: Document | undefined;
    let observer: MutationObserver | undefined;
    let sheet: HTMLStyleElement | undefined;
    let scheduled = false, disposed = false;
    const entries = new Map<HTMLElement, { root: HTMLElement; button: HTMLButtonElement; account: HTMLElement; label: HTMLElement; status: HTMLElement; host: HTMLElement; id: number; dual: boolean; native?: HTMLElement; nativeTitle?: string | null; titleApplied?: string; target?: string }>();
    const pending = new Set<number>();
    function remove(section: HTMLElement) {
        const entry = entries.get(section);
        entry?.root.remove();
        if (entry?.native && entry.native.getAttribute('title') === entry.titleApplied) {
            if (entry.nativeTitle == null) entry.native.removeAttribute('title'); else entry.native.setAttribute('title', entry.nativeTitle);
        }
        entry?.host.classList.remove('SteamFusionRouted', 'SteamFusionSingleRoute'); entries.delete(section);
    }
    function refresh() {
        if (disposed) return;
        const next = getDocument();
        if (next !== doc) {
            observer?.disconnect();
            for (const section of entries.keys()) remove(section);
            sheet?.remove();
            doc = next;
            if (doc?.body) {
                sheet = doc.createElement('style');
                sheet.textContent = `
                    .SteamFusionRoute { display:flex; position:relative; flex:none; height:100%; margin-inline-end:8px; border-radius:2px; }
                    .SteamFusionRoute > button { font-family:inherit; border-radius:2px; }
                    .SteamFusionRoute > button:disabled { opacity:.55; cursor:wait; }
                    .SteamFusionRoute > button:focus-visible { outline:2px solid white; outline-offset:2px; }
                    .SteamFusionRouteAccount { display:none; }
                    .SteamFusionSingleRoute > .PlayButton { display:none !important; }
                    .SteamFusionAlternateRoute { margin-inline:8px 0; background:#ffffff12; }
                    .SteamFusionAlternateRoute > button { color:#dce5ee; padding:0 16px; font-size:16px; background:transparent; }
                    .SteamFusionAlternateRoute .ButtonText { font-size:16px; letter-spacing:0; }
                    .SteamFusionAlternateRoute:hover { background:#ffffff22; }
                    .SteamFusionAlternateRoute svg, .SteamFusionAlternateRoute .SteamFusionRouteAccount { display:none; }
                    .SteamFusionRouteStatus { position:absolute; top:calc(100% + 4px); left:0; z-index:10; font-size:12px; color:#dfe7ee; background:#202630; border-radius:2px; white-space:nowrap; padding:4px 8px; pointer-events:none; }
                    .SteamFusionRouteStatus:empty { display:none; }
                `;
                doc.head.append(sheet);
                observer = new MutationObserver(() => {
                    if (scheduled) return;
                    scheduled = true;
                    queueMicrotask(() => { scheduled = false; refresh(); });
                });
                observer.observe(doc.body, { childList: true, subtree: true });
            }
        }
        const classes = nativeClasses();
        for (const section of entries.keys()) if (!section.isConnected || !classes?.Green) remove(section);
        if (!classes?.Green || !classes.PlayButton || !classes.ButtonChild || !classes.ButtonText) return;
        for (const section of Array.from(doc?.querySelectorAll<HTMLElement>('.PlayBar .ActionSection') ?? [])) {
            const host = section.querySelector<HTMLElement>('.PlayButtonContainer');
            const id = appIdFor(section);
            const mapping = id === null ? undefined : mappingFor(id);
            const dual = mapping ? dualAccounts(mapping, identity()) : [];
            const nativeButton = host?.querySelector<HTMLElement>(':scope > .PlayButton');
            if (!host || id === null || !mapping || (!dual.length && !needsRoute(mapping, identity())) || (dual.length && !nativeButton)) { remove(section); continue; }
            let entry = entries.get(section);
            if (entry?.id !== id || entry.root.parentElement !== host || entry.dual !== !!dual.length || (entry.native && entry.native !== nativeButton)) { remove(section); entry = undefined; }
            if (!entry) {
                const root = doc!.createElement('div');
                root.dataset.steamfusion = 'route';
                root.className = `SteamFusionRoute ${dual.length ? 'SteamFusionAlternateRoute' : classes.Green}`;
                const button = doc!.createElement('button');
                button.type = 'button';
                button.className = `${classes.PlayButton} ${classes.ButtonChild} Focusable`;
                const icon = doc!.createElementNS('http://www.w3.org/2000/svg', 'svg');
                icon.setAttribute('viewBox', '0 0 24 24'); icon.setAttribute('aria-hidden', 'true');
                const path = doc!.createElementNS('http://www.w3.org/2000/svg', 'path');
                path.setAttribute('d', 'M7 3L21 12L7 21Z'); path.setAttribute('fill', 'currentColor'); icon.append(path);
                const label = doc!.createElement('span'); label.className = classes.ButtonText; label.textContent = '开始游戏';
                button.append(icon, label);
                const account = doc!.createElement('span'); account.className = 'SteamFusionRouteAccount';
                const status = doc!.createElement('span');
                status.setAttribute('role', 'status'); status.className = 'SteamFusionRouteStatus';
                root.append(button, account, status);
                if (dual.length) {
                    host.append(root);
                } else { host.classList.add('SteamFusionSingleRoute'); host.prepend(root); }
                entry = { root, button, account, label, status, host, id, dual: !!dual.length,
                    native: dual.length ? nativeButton! : undefined, nativeTitle: nativeButton?.getAttribute('title') };
                entries.set(section, entry);
                host.classList.add('SteamFusionRouted');
                button.addEventListener('click', async event => {
                    event.preventDefault(); event.stopPropagation();
                    // Recheck the live page and rules: React can reuse this DOM for another game.
                    const liveId = appIdFor(section);
                    const live = liveId === null ? undefined : mappingFor(liveId);
                    const liveDual = live ? dualAccounts(live, identity()) : [];
                    const shown = entries.get(section);
                    if (!section.isConnected || liveId !== id || !live || !shown ||
                        (shown.dual ? liveDual[1]?.steamId !== shown.target : liveDual.length > 0 || !needsRoute(live, identity()))) { refresh(); return; }
                    if (pending.has(id)) return;
                    pending.add(id); button.disabled = true;
                    status.textContent = '正在提交…';
                    try {
                        report(`从 Steam 详情页提交游戏 ${id}。`);
                        await send(id, shown.dual ? shown.target : undefined);
                        status.textContent = '已提交，请留意 SteamFusion 提示';
                    } catch (error) { status.textContent = '提交失败，请打开 SteamFusion 查看'; report(String(error)); }
                    finally { pending.delete(id); button.disabled = false; }
                });
            }
            const selected = dual[1];
            entry.target = selected?.steamId;
            const name = (selected?.accountName ?? mapping.accountName)?.split(' · ')[0]?.trim();
            const label = name ? `使用${name}启动` : '使用对应账号启动';
            if (entry.dual) {
                const title = `使用当前账号 ${dual[0].accountName} 启动`;
                if (entry.native!.getAttribute('title') !== title) {
                    if (entry.native!.getAttribute('title') !== entry.titleApplied) entry.nativeTitle = entry.native!.getAttribute('title');
                    entry.native!.setAttribute('title', title);
                }
                entry.titleApplied = title;
            }
            if (entry.account.textContent !== (name || '指定账号')) entry.account.textContent = name || '指定账号';
            entry.button.setAttribute('aria-label', label);
            entry.account.title = mapping.accountName ?? '使用已配置的账号';
            entry.button.title = selected ? `仅本次使用 ${selected.accountName} 启动，不修改默认路由` : mapping.accountName ? `使用 ${mapping.accountName} 启动此游戏` : '使用已配置的账号和运行环境启动';
        }
    }
    return { refresh, dispose() {
        disposed = true; observer?.disconnect(); sheet?.remove();
        for (const section of entries.keys()) remove(section);
    } };
}
