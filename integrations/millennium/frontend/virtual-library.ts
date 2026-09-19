import type { Mapping } from './routing';
import { artworkUrl, filterGames } from './uninstalled.ts';

export type VirtualLibraryModel = { enabled: boolean; user: string | null; games: Mapping[]; loading: boolean; error?: string };
/** Mount only our own DOM; no shortcuts, collection records or Steam ownership data are written. */
export function installVirtualLibrary(options: {
    document: () => Document | undefined; navigationClass: () => string | undefined;
    model: () => VirtualLibraryModel; download: (appId: number) => Promise<void>; refreshData: () => void;
}) {
    let doc: Document | undefined, button: HTMLButtonElement | undefined, panel: HTMLElement | undefined;
    let style: HTMLStyleElement | undefined, grid: HTMLElement | undefined, summary: HTMLElement | undefined;
    let status: HTMLElement | undefined, search: HTMLInputElement | undefined, more: HTMLElement | undefined;
    let drawer: HTMLElement | undefined, sidebar: HTMLElement | undefined;
    let shown = false, disposed = false, query = '', limit = 60, lastKey = '', pending = false, previousUser: string | null = null;
    let observer: ResizeObserver | undefined;
    let scroll: HTMLElement | undefined, renderedKey = '';
    let loadTimer: ReturnType<typeof setTimeout> | undefined;
    function cancelLoad() { if (loadTimer !== undefined) clearTimeout(loadTimer); loadTimer = undefined; }
    function scheduleLoad() {
        if (disposed || !shown || loadTimer !== undefined) return;
        loadTimer = setTimeout(() => {
            loadTimer = undefined;
            const model = options.model();
            if (disposed || !shown || !scroll?.isConnected || !model.enabled || model.user !== previousUser || model.loading || model.error) return;
            if (scroll.clientHeight <= 0 || scroll.scrollHeight - scroll.scrollTop - scroll.clientHeight > 480) return;
            const total = filterGames(model.games, query).length;
            if (limit >= total) return;
            limit = Math.min(limit + 60, total);
            render(true);
        }, 16);
    }
    const el = <K extends keyof HTMLElementTagNameMap>(tag: K, cls = '', text?: string): HTMLElementTagNameMap[K] => {
        const node = doc!.createElement(tag); node.className = cls; if (text != null) node.textContent = text; return node;
    };
    const action = (text: string, fn: () => void, cls = '') => { const b = el('button', cls, text); b.type = 'button'; b.addEventListener('click', fn); return b; };
    function close() { cancelLoad(); shown = false; if (panel) panel.hidden = true; button?.setAttribute('aria-expanded', 'false'); drawer?.remove(); drawer = undefined; }
    function position() {
        if (!panel || !sidebar) return;
        const rect = sidebar.getBoundingClientRect();
        if (rect.width < 100 || rect.height < 100) { close(); return; }
        panel.style.left = `${rect.right}px`; panel.style.top = `${rect.top}px`;
        panel.style.width = `${Math.max(0, doc!.documentElement.clientWidth - rect.right)}px`;
        panel.style.height = `${rect.height}px`;
        scheduleLoad();
    }
    function outside(event: Event) {
        const path = event.composedPath();
        if (shown && !path.includes(panel!) && !path.includes(button!)) close();
    }
    function keyboard(event: KeyboardEvent) { if (event.key === 'Escape' && shown) { close(); button?.focus(); } }
    function picture(game: Mapping, kind: 'cover' | 'hero') {
        const img = el('img'); img.alt = ''; img.loading = 'lazy'; img.decoding = 'async'; img.referrerPolicy = 'no-referrer';
        img.src = artworkUrl(game.appId, kind);
        let fallback = false;
        img.addEventListener('error', () => { if (!fallback) { fallback = true; img.src = artworkUrl(game.appId, 'header'); img.classList.add('sfvl-fallback'); } else img.hidden = true; });
        return img;
    }
    async function download(game: Mapping, target: HTMLButtonElement) {
        const model = options.model();
        if (pending || !model.enabled || model.loading || model.error || model.user !== previousUser || !model.games.some(g => g.appId === game.appId && g.steamId === game.steamId)) return;
        pending = true; target.disabled = true;
        const user = model.user; const label = target.textContent;
        status!.textContent = `正在打开 ${game.gameName} 的所属账号页面…`;
        try {
            await options.download(game.appId);
            if (options.model().user === user) status!.textContent = '请求已提交，请在 Steam 中确认账号并选择安装目录。';
        } catch (error) { if (options.model().user === user) status!.textContent = `打开失败：${String(error)}`; }
        finally { pending = false; target.disabled = false; target.textContent = label; }
    }
    function showDetails(game: Mapping) {
        drawer?.remove(); drawer = el('aside', 'sfvl-detail'); drawer.setAttribute('aria-label', game.gameName!);
        const top = el('div', 'sfvl-hero'); top.append(picture(game, 'hero'), action('×', () => { drawer?.remove(); drawer = undefined; }, 'sfvl-dismiss'));
        const body = el('div', 'sfvl-detail-body');
        body.append(el('span', 'sfvl-eyebrow', '来自另一账号'), el('h2', '', game.gameName), el('p', 'sfvl-muted', game.accountName || '所属账号'));
        const go = action('到所属账号下载', () => void download(game, go), 'sfvl-primary');
        body.append(go, el('p', 'sfvl-note', '打开对应 Steam 的游戏页面，选择安装目录后下载。'),
            el('p', 'sfvl-note', '安装完成后，此游戏会自动从本页移出。'), el('span', 'sfvl-appid', `APP ${game.appId}`));
        drawer.append(top, body); panel!.append(drawer); go.focus();
    }
    function render(append = false) {
        if (!grid || !shown) return;
        const model = options.model(); const matches = filterGames(model.games, query);
        const key = JSON.stringify(matches.map(g => [g.appId, g.gameName, g.accountName]));
        if (key !== renderedKey || model.loading || model.error) append = false;
        const start = append ? grid.children.length : 0;
        if (!append) { grid.replaceChildren(); drawer?.remove(); drawer = undefined; }
        renderedKey = key;
        summary!.textContent = model.loading ? '正在读取安装状态…' : `${model.games.length} 款未安装游戏${query ? ` · 找到 ${matches.length} 款` : ''}`;
        if (model.error) { grid.append(el('p', 'sfvl-empty', model.error)); more!.hidden = true; return; }
        if (!matches.length) grid.append(el('p', 'sfvl-empty', model.loading ? '正在读取游戏库，请稍候。' : query ? '没有找到匹配的游戏。' : '目前没有未安装的游戏。新购入游戏需要先更新 SteamFusion 的游戏库记录。'));
        for (const game of matches.slice(start, limit)) {
            const card = action('', () => showDetails(game), 'sfvl-card'); card.title = game.gameName!; card.setAttribute('aria-label', game.gameName!);
            const art = el('div', 'sfvl-art'); art.append(el('span', 'sfvl-placeholder', game.gameName), picture(game, 'cover'));
            const meta = el('div', 'sfvl-card-meta'); meta.append(el('strong', '', game.gameName), el('span', '', '查看与下载 ↗'));
            card.append(art, meta); grid.append(card);
        }
        more!.hidden = matches.length === 0;
        more!.textContent = matches.length <= limit ? `已显示全部 ${matches.length} 款游戏` : `已显示 ${limit} / ${matches.length} 款 · 向下滚动继续加载`;
        scheduleLoad();
    }
    function open() {
        shown = true; panel!.hidden = false; button!.setAttribute('aria-expanded', 'true'); position(); render(); search?.focus();
        options.refreshData();
    }
    function unmount() {
        cancelLoad(); scroll?.removeEventListener('scroll', scheduleLoad); scroll = undefined; renderedKey = '';
        observer?.disconnect(); observer = undefined; doc?.removeEventListener('pointerdown', outside, true); doc?.removeEventListener('keydown', keyboard);
        doc?.defaultView?.removeEventListener('resize', position); button?.remove(); panel?.remove(); style?.remove();
        button = undefined; panel = undefined; sidebar = undefined; shown = false; lastKey = '';
    }
    function mount(navigation: HTMLElement) {
        sidebar = navigation.parentElement ?? undefined; if (!sidebar) return;
        style = el('style'); style.textContent = styles; doc!.head.append(style);
        button = action('▦  另一账号 · 未安装', () => shown ? close() : open(), 'sfvl-nav'); button.setAttribute('aria-expanded', 'false');
        navigation.after(button);
        panel = el('section', 'sfvl-page'); panel.hidden = true; panel.setAttribute('aria-label', 'SteamFusion 未安装游戏');
        const head = el('header', 'sfvl-header'); const title = el('div');
        title.append(el('span', 'sfvl-eyebrow', 'STEAMFUSION LIBRARY'), el('h1', '', '留给下一次冒险'), summary = el('p', 'sfvl-muted'));
        head.append(title, action('返回游戏库', () => close(), 'sfvl-secondary'));
        const controls = el('div', 'sfvl-controls'); search = el('input'); search.type = 'search'; search.placeholder = '搜索游戏名称或 AppID'; search.setAttribute('aria-label', '搜索另一账号未安装游戏');
        search.value = query; search.addEventListener('input', () => { cancelLoad(); query = search!.value; limit = 60; if (scroll) scroll.scrollTop = 0; render(); });
        controls.append(search, action('刷新', () => { options.refreshData(); status!.textContent = '正在刷新安装状态…'; }, 'sfvl-secondary'));
        status = el('p', 'sfvl-status'); status.setAttribute('role', 'status');
        scroll = el('div', 'sfvl-scroll'); grid = el('div', 'sfvl-grid'); more = el('p', 'sfvl-more'); more.setAttribute('role', 'status'); scroll.append(grid, more);
        scroll.addEventListener('scroll', scheduleLoad, { passive: true });
        panel.append(head, controls, status, scroll); doc!.body.append(panel);
        doc!.addEventListener('pointerdown', outside, true); doc!.addEventListener('keydown', keyboard); doc!.defaultView?.addEventListener('resize', position);
        observer = new ResizeObserver(position); observer.observe(sidebar);
    }
    function refresh() {
        if (disposed) return;
        const next = options.document(); const model = options.model();
        if (next !== doc) { unmount(); doc = next; }
        if (model.user !== previousUser) { close(); query = ''; limit = 60; previousUser = model.user; }
        if (!doc?.body || !model.enabled) { unmount(); return; }
        const cls = options.navigationClass(); const navigation = cls ? doc.getElementsByClassName(cls)[0] as HTMLElement | undefined : undefined;
        if (!navigation) { unmount(); return; }
        if (!button?.isConnected || button.previousElementSibling !== navigation) { unmount(); mount(navigation); }
        if (!button || !panel) return;
        const label = model.games[0]?.accountName?.split(' · ')[0]?.trim() || '另一账号';
        const navText = `▦  ${label} · 未安装`;
        if (button.textContent !== navText) button.textContent = navText;
        const key = JSON.stringify([model.user, model.games.map(g => [g.appId, g.gameName, g.accountName]), model.loading, model.error]);
        if (key !== lastKey) { lastKey = key; render(); }
        if (shown) position();
    }
    return { refresh, open: () => { refresh(); if (panel) open(); }, dispose: () => { disposed = true; unmount(); } };
}
const styles = `
.sfvl-nav{display:block;box-sizing:border-box;width:calc(100% - 16px);margin:4px 8px 8px;padding:10px 12px;border:1px solid #405267;border-radius:4px;background:linear-gradient(110deg,#263b51,#23303f);color:#c9eaff;font:inherit;text-align:left;cursor:pointer;flex:none}
.sfvl-nav:hover,.sfvl-nav[aria-expanded=true]{background:#304e69;color:#fff;border-color:#63b8ed}
.sfvl-page{position:fixed;z-index:20;box-sizing:border-box;display:flex;flex-direction:column;overflow:hidden;background:radial-gradient(ellipse at 80% 0,#243d53 0,transparent 55%),#18212d;color:#e6edf5;font-family:inherit;isolation:isolate}
.sfvl-page[hidden]{display:none}.sfvl-page *{box-sizing:border-box}.sfvl-page button,.sfvl-page input{font:inherit}.sfvl-page button{cursor:pointer}.sfvl-page button:focus-visible,.sfvl-nav:focus-visible{outline:2px solid #81caff;outline-offset:2px}.sfvl-header{display:flex;justify-content:space-between;align-items:center;gap:16px;padding:28px 28px 12px}.sfvl-eyebrow{font-size:10px;letter-spacing:2px;color:#74b8d7}.sfvl-header h1{font-size:28px;font-weight:500;margin:9px 0}.sfvl-muted{color:#9eafbf;font-size:13px;margin:8px 0}.sfvl-controls{display:flex;gap:10px;padding:4px 28px 8px}.sfvl-controls input{min-width:0;flex:1;background:#111a25;border:1px solid #34485a;border-radius:5px;padding:11px 14px;color:#e6edf5;outline:none}.sfvl-controls input:focus{border-color:#67b8e7}.sfvl-secondary{border:1px solid #41566b;border-radius:4px;background:#2b3b4d;color:#cdddea;padding:9px 14px;white-space:nowrap}.sfvl-secondary:hover{background:#3a5168}.sfvl-status{font-size:12px;color:#9fc9de;margin:0;padding:0 28px 10px;min-height:10px}.sfvl-scroll{overflow:auto;flex:1;padding:8px 28px 28px;min-height:0}.sfvl-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(145px,1fr));gap:20px 16px;align-content:start}.sfvl-card{padding:0;border:0;min-width:0;border-radius:6px;overflow:hidden;background:#223041;color:inherit;text-align:left;box-shadow:0 6px 15px #0003;transition:transform .14s,box-shadow .14s}.sfvl-card:hover{transform:translateY(-3px);box-shadow:0 8px 22px #0005}.sfvl-art{aspect-ratio:2/3;position:relative;background:linear-gradient(145deg,#3c5b73,#152030);overflow:hidden}.sfvl-art img{position:absolute;inset:0;width:100%;height:100%;object-fit:cover}.sfvl-placeholder{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;padding:18px;color:#c4d6e8;font-size:19px;line-height:1.5;text-align:center}.sfvl-art img.sfvl-fallback{object-fit:contain;background:#182330}.sfvl-card-meta{padding:11px 12px;display:flex;flex-direction:column;gap:6px}.sfvl-card-meta strong{font-size:13px;font-weight:500;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.sfvl-card-meta span{font-size:11px;color:#91b6ce}.sfvl-more{display:block;margin:24px auto 0;text-align:center;color:#91b6ce;font-size:12px}.sfvl-more[hidden]{display:none}.sfvl-empty{grid-column:1/-1;color:#a4b5c6;padding:40px 10px;text-align:center;line-height:1.8}.sfvl-detail{position:absolute;inset:0 0 0 auto;width:min(390px,100%);z-index:2;background:#1b2938;border-left:1px solid #415164;box-shadow:-18px 0 70px #0008;overflow:auto}.sfvl-hero{height:190px;position:relative;background:#273e53}.sfvl-hero img{width:100%;height:100%;object-fit:cover}.sfvl-dismiss{position:absolute;top:12px;right:12px;border:0;border-radius:50%;width:32px;height:32px;background:#111c29dc;color:#fff;font-size:22px!important}.sfvl-detail-body{padding:26px}.sfvl-detail h2{font-size:25px;line-height:1.4;margin:12px 0 6px}.sfvl-primary{display:block;width:100%;margin:26px 0 18px;border:0;border-radius:4px;padding:13px;background:linear-gradient(100deg,#2a94c8,#3376c9);color:white;font-weight:600!important}.sfvl-primary:hover{filter:brightness(1.15)}.sfvl-primary:disabled{opacity:.5;cursor:wait}.sfvl-note{font-size:13px;line-height:1.8;color:#9cadbd}.sfvl-appid{display:block;margin-top:30px;color:#718ba0;font-size:11px;letter-spacing:1px}
`;
