import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseAppId, needsRoute, hookRunGame } from '../frontend/routing.ts';

const mapping = { appId: 368340, steamId: '76561198000000002', mode: 'auto', enabled: true };
test('non-Steam 64-bit GameIDs remain intact and are never routed as an AppID', () => {
    assert.equal(parseAppId('15826960184098930688'), null);
    assert.equal(parseAppId('368340'), 368340);
    assert.equal(parseAppId('368340; calc'), null);
    assert.equal(parseAppId(-1), null);
});
test('route according to both account and environment', () => {
    assert.equal(needsRoute(mapping, { steamId: mapping.steamId, environment: 'native' }), false);
    assert.equal(needsRoute(mapping, { steamId: null, environment: 'native' }), true);
    assert.equal(needsRoute({ ...mapping, mode: 'nativeOnly' }, { steamId: mapping.steamId, environment: 'Box_B' }), true);
    assert.equal(needsRoute({ ...mapping, fixedEnvironment: 'native' }, { steamId: mapping.steamId, environment: 'Box_B' }), true);
    assert.equal(needsRoute({ ...mapping, fixedEnvironment: 'Box_B' }, { steamId: mapping.steamId, environment: 'native' }), true);
});
test('unmapped and local launches retain original this, arguments and return value', () => {
    const apps = { RunGame(...args: unknown[]) { assert.equal(this, apps); return args; } };
    const original = apps.RunGame;
    const dispose = hookRunGame(apps, id => id === 368340 ? mapping : undefined,
        () => ({ steamId: mapping.steamId, environment: 'native' }), async () => assert.fail(), () => {});
    assert.deepEqual(apps.RunGame('368340', '', -1, 7), ['368340', '', -1, 7]);
    assert.deepEqual(apps.RunGame('15826960184098930688'), ['15826960184098930688']);
    dispose(); assert.equal(apps.RunGame, original);
});
test('routed failure does not accidentally fall through to wrong-account launch', async () => {
    let original = 0, sent = 0, reported = 0;
    const apps = { RunGame() { original++; } };
    const dispose = hookRunGame(apps, () => mapping, () => ({ steamId: 'different', environment: 'native' }),
        async () => { sent++; throw new Error('IPC failed'); }, () => { reported++; });
    apps.RunGame('368340');
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(original, 0); assert.equal(sent, 1); assert.equal(reported, 1); dispose();
});
test('double-click while submitting does not submit twice', async () => {
    let sent = 0; let finish!: () => void;
    const wait = new Promise<void>(resolve => { finish = resolve; });
    const apps = { RunGame(..._args: unknown[]) {} };
    const dispose = hookRunGame(apps, () => mapping, () => ({ steamId: null, environment: 'native' }),
        async () => { sent++; await wait; }, () => {});
    apps.RunGame('368340'); apps.RunGame('368340'); assert.equal(sent, 1);
    finish(); await wait; dispose();
});
test('unload does not overwrite a later plugin hook', () => {
    const apps = { RunGame() {} };
    const dispose = hookRunGame(apps, () => undefined, () => ({ steamId: null, environment: 'native' }), async () => {}, () => {});
    const other = () => {}; apps.RunGame = other; dispose(); assert.equal(apps.RunGame, other);
});
