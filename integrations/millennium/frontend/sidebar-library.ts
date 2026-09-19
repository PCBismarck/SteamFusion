import type { Mapping } from './routing';

/** A plugin-owned group alongside Steam's original list; never writes collection or shortcut data. */
export function createSidebarLibrary(doc: Document, overviewButton: HTMLButtonElement, options: {
    search: (query: string) => void; select: (appId: number) => void;
}) {
    const element = <K extends keyof HTMLElementTagNameMap>(tag: K, cls: string, text?: string) => {
        const node = doc.createElement(tag); node.className = cls; if (text !== undefined) node.textContent = text; return node;
    };
    const root = element('section', 'sfvl-side-group'); root.setAttribute('aria-label', '另一账号未安装游戏');
    const header = element('div', 'sfvl-side-header');
    const toggle = element('button', 'sfvl-side-toggle'); toggle.type = 'button';
    const body = element('div', 'sfvl-side-body');
    const search = element('input', 'sfvl-side-search'); search.type = 'search'; search.placeholder = '搜索本分组';
    search.setAttribute('aria-label', '搜索侧栏未安装游戏');
    const count = element('div', 'sfvl-side-count'); count.setAttribute('role', 'status');
    const list = element('nav', 'sfvl-side-list'); list.setAttribute('aria-label', '未安装游戏快速浏览');
    header.append(toggle, overviewButton); body.append(search, count, list); root.append(header, body);
    let user: string | null | undefined, expanded = false, selected: number | null = null, key = '', lastQuery = '', lastOrder = '';
    const rows = new Map<number, HTMLButtonElement>();
    const storageKey = () => `steamfusion.uninstalled.sidebar.v1:${user ?? 'anonymous'}`;
    function expand(value: boolean) {
        expanded = value; body.hidden = !expanded;
        toggle.setAttribute('aria-expanded', String(expanded));
        toggle.setAttribute('aria-label', expanded ? '收起未安装游戏列表' : '展开未安装游戏列表');
        toggle.title = expanded ? '收起游戏列表' : '展开游戏列表';
    }
    expand(false);
    toggle.addEventListener('click', () => {
        expand(!expanded);
        try { doc.defaultView?.localStorage.setItem(storageKey(), String(expanded)); } catch { /* Session-only state is sufficient. */ }
    });
    search.addEventListener('input', () => options.search(search.value));
    list.addEventListener('keydown', event => {
        const items = [...rows.values()]; const current = items.findIndex(row => row === event.target);
        if (current < 0 || !['ArrowUp', 'ArrowDown', 'Home', 'End'].includes(event.key)) return;
        event.preventDefault();
        const index = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1 :
            Math.max(0, Math.min(items.length - 1, current + (event.key === 'ArrowDown' ? 1 : -1)));
        const next = items[index]; next.focus(); next.scrollIntoView?.({ block: 'nearest' }); next.click();
    });
    function select(appId: number | null) {
        if (selected !== null) rows.get(selected)?.removeAttribute('aria-current');
        selected = appId;
        if (selected !== null) rows.get(selected)?.setAttribute('aria-current', 'page');
    }
    function update(state: { user: string | null; games: Mapping[]; query: string; order: string; total: number; loading: boolean; error?: string }) {
        if (state.user !== user) {
            user = state.user; key = ''; selected = null;
            try { expand(doc.defaultView?.localStorage.getItem(storageKey()) === 'true'); } catch { expand(false); }
            list.scrollTop = 0;
        }
        if (lastQuery !== state.query || lastOrder !== state.order) list.scrollTop = 0;
        lastQuery = state.query; lastOrder = state.order;
        if (search.value !== state.query) { search.value = state.query; list.scrollTop = 0; }
        const nextKey = JSON.stringify([state.user, state.query, state.games.map(g => [g.appId, g.gameName]), state.total, state.loading, state.error]);
        if (nextKey === key) return;
        key = nextKey;
        count.textContent = state.error ? '安装状态暂不可用' : state.loading ? '正在读取安装状态…' :
            state.query ? `${state.games.length} / ${state.total} 款` : `${state.total} 款 · 单击查看详情`;
        const position = list.scrollTop;
        const activeId = [...rows].find(([, row]) => row === doc.activeElement)?.[0];
        list.replaceChildren(); rows.clear();
        if (state.error || state.loading || !state.games.length) {
            list.append(element('p', 'sfvl-side-empty', state.error || (state.loading ? '请稍候…' : state.query ? '没有匹配的游戏' : '暂无未安装游戏')));
            selected = null; return;
        }
        const fragment = doc.createDocumentFragment();
        for (const game of state.games) {
            const row = element('button', 'sfvl-side-game', game.gameName); row.type = 'button';
            row.title = `${game.gameName} · AppID ${game.appId}`; row.dataset.appid = String(game.appId);
            row.addEventListener('click', () => options.select(game.appId));
            rows.set(game.appId, row); fragment.append(row);
        }
        list.append(fragment); select(selected);
        if (activeId !== undefined) rows.get(activeId)?.focus({ preventScroll: true });
        list.scrollTop = position;
    }
    return { root, update, select, dispose: () => { root.remove(); rows.clear(); } };
}

export const sidebarStyles = `
.sfvl-side-group{box-sizing:border-box;flex:none;min-width:0;margin:4px 8px 8px;font:inherit;color:#b8c6d4}
.sfvl-side-header{display:flex;align-items:stretch;min-width:0;background:#253649;border:1px solid #405267;border-radius:4px;overflow:hidden}
.sfvl-side-header .sfvl-nav{flex:1;min-width:0;width:auto;margin:0;border:0;border-radius:0;padding:9px 6px;background:transparent;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.sfvl-side-toggle{flex:none;width:27px;border:0;background:transparent;color:#aac9df;font:inherit;cursor:pointer;padding:0;font-size:17px}
.sfvl-side-toggle::before{content:"";display:inline-block;width:6px;height:6px;border-right:2px solid currentColor;border-bottom:2px solid currentColor;transform:rotate(-45deg)}.sfvl-side-toggle[aria-expanded=true]::before{transform:rotate(45deg);vertical-align:3px}
.sfvl-side-toggle:hover,.sfvl-side-header .sfvl-nav:hover{background:#304e69;color:#fff}
.sfvl-side-body{padding-top:7px}.sfvl-side-body[hidden]{display:none}
.sfvl-side-search{box-sizing:border-box;width:100%;min-width:0;padding:7px 9px;border:1px solid #394a5a;border-radius:3px;background:#171e27;color:#dce5ee;font:inherit;font-size:12px}
.sfvl-side-count{font-size:11px;color:#8298aa;padding:6px 3px}
.sfvl-side-list{display:block;max-height:min(32vh,340px);overflow:auto;overscroll-behavior:contain;border-bottom:1px solid #354250;padding-bottom:5px}
.sfvl-side-game{box-sizing:border-box;display:block;width:100%;min-width:0;padding:6px 9px;border:0;border-left:2px solid transparent;background:transparent;color:#b8c6d4;font:inherit;font-size:13px;line-height:1.35;text-align:left;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;cursor:pointer}
.sfvl-side-game:hover{background:#ffffff0d;color:#fff}.sfvl-side-game[aria-current=page]{background:#32516b;color:#fff;border-left-color:#6bbdef}
.sfvl-side-group button:focus-visible,.sfvl-side-search:focus-visible{outline:2px solid #81caff;outline-offset:-2px}
.sfvl-side-empty{margin:0;padding:10px 6px;color:#91a4b5;font-size:12px;line-height:1.5;overflow-wrap:anywhere}
`;
