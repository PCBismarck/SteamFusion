import test from 'node:test';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';
import { installDetailsButtons } from '../frontend/details.ts';
import { hookRunGame, dualAccounts, decodeLaunchOptions } from '../frontend/routing.ts';
const main = '76561198000000001', other = '76561198000000002';
const expiresAt = new Date(Date.now() + 3600000).toISOString();
const choices = [{ steamId: other, accountName: '小号', expiresAt }, { steamId: main, accountName: '大号', expiresAt }];
const mapping = () => ({ appId: 10, steamId: other, accountName: '小号', gameName: 'Game', mode: 'auto', enabled: true, launchAccounts: choices });
test('dual options reject stale malformed duplicate and unknown identities', () => {
    const now = Date.now();
    const report = { schemaVersion: 1, capturedAt: new Date(now).toISOString(), games: [{ appId: 10, accounts: choices }] };
    assert.equal(decodeLaunchOptions(report, now).get(10)?.length, 2);
    for (const bad of [null, { ...report, capturedAt: 'bad' }, { ...report, capturedAt: new Date(now - 100000).toISOString() },
        { ...report, games: [{ appId: 10, accounts: [choices[0], choices[0]] }] }]) assert.throws(() => decodeLaunchOptions(bad, now));
    assert.equal(dualAccounts(mapping(), { steamId: null, environment: 'native' }).length, 0);
    assert.equal(dualAccounts(mapping(), { steamId: main, environment: 'Box_A' }).length, 0);
    assert.equal(dualAccounts({ ...mapping(), fixedEnvironment: 'native' }, { steamId: main, environment: 'native' }).length, 0);
    assert.equal(dualAccounts(mapping(), { steamId: main, environment: 'native' }, now + 7200000).length, 0);
});
test('expired dual-account buttons cannot silently fall back to the saved foreign route', () => {
    let launched = 0, warned = 0;
    const apps = { RunGame: () => { launched++; } };
    const expired = { ...mapping(), launchAccounts: choices.map(a => ({ ...a, expiresAt: new Date(0).toISOString() })) };
    const unhook = hookRunGame(apps, () => expired, () => ({ steamId: main, environment: 'native' }), async () => { launched++; }, () => { warned++; });
    apps.RunGame(10);
    assert.equal(launched, 0); assert.equal(warned, 1); unhook();
});
test('left native button launches current account and right launches the other without changing the saved mapping', async () => {
    const dom = new JSDOM('<html><head></head><body><div class="PlayBar"><div class="ActionSection"><div class="PlayButtonContainer"><button class="PlayButton Green"><span class="ButtonText">开始游戏</span></button></div></div></div></body></html>');
    const doc = dom.window.document;
    const previousObserver = globalThis.MutationObserver;
    globalThis.MutationObserver = dom.window.MutationObserver;
    const root: any = { stateNode: {} }; root.stateNode.current = root;
    const fiber: any = { memoizedProps: { appid: 10 }, return: root };
    const section: any = doc.querySelector('.ActionSection'); section.__reactFiber$test = fiber;
    let current = main, currentMapping = mapping();
    const requests: any[] = []; let originals = 0;
    const send = async (id: number, steamId?: string) => { requests.push([id, steamId]); };
    const apps = { RunGame: () => { originals++; } };
    const unhook = hookRunGame(apps, () => currentMapping, () => ({ steamId: current, environment: 'native' }), send, () => {});
    const native: any = doc.querySelector('.PlayButton'); native.addEventListener('click', () => apps.RunGame(10));
    const view = installDetailsButtons(() => doc, () => currentMapping, () => ({ steamId: current, environment: 'native' }), send, () => {},
        () => ({ Green: 'Green', PlayButton: 'PlayButton', ButtonChild: 'ButtonChild', ButtonText: 'ButtonText' }));
    try {
        view.refresh();
        assert.equal(doc.querySelectorAll('.SteamFusionRoute').length, 1);
        assert.equal(native.title, '使用当前账号 大号 启动');
        assert.equal(native.textContent, '开始游戏');
        const right: any = doc.querySelector('.SteamFusionRoute button');
        assert.equal(right.textContent, '开始游戏');
        assert.equal(right.title, '仅本次使用 小号 启动，不修改默认路由');
        native.click(); await new Promise(r => setImmediate(r)); right.click(); await new Promise(r => setImmediate(r));
        assert.deepEqual(requests, [[10, main], [10, other]]); assert.equal(originals, 0); assert.equal(currentMapping.steamId, other);
        current = other;
        right.click(); await new Promise(r => setImmediate(r));
        assert.equal(requests.length, 2, 'old button must not submit after account change');
        view.refresh();
        assert.equal(native.title, '使用当前账号 小号 启动');
        assert.equal(doc.querySelector('.SteamFusionRoute button')?.getAttribute('title'), '仅本次使用 大号 启动，不修改默认路由');
        (doc.querySelector('.SteamFusionRoute button') as any).click(); await new Promise(r => setImmediate(r));
        assert.deepEqual(requests.at(-1), [10, main]);
        current = main; currentMapping = { ...currentMapping, launchAccounts: [] }; view.refresh();
        assert.ok(doc.querySelector('.SteamFusionSingleRoute')); assert.equal(native.getAttribute('title'), null);
        (doc.querySelector('.SteamFusionRoute button') as any).click(); await new Promise(r => setImmediate(r));
        assert.deepEqual(requests.at(-1), [10, undefined]);
        currentMapping = { ...currentMapping, steamId: main }; view.refresh();
        assert.equal(doc.querySelector('.SteamFusionRoute'), null); assert.equal(native.isConnected, true);
    } finally { view.dispose(); unhook(); globalThis.MutationObserver = previousObserver; dom.window.close(); }
    assert.equal(doc.querySelector('.SteamFusionCurrentAccount'), null);
    assert.equal(doc.querySelector('.SteamFusionRouted'), null);
});
