import { test } from 'node:test';
import assert from 'node:assert/strict';
import { captureLibrary } from '../frontend/catalog.ts';
const user = '76561198000000001';
const game = { appid: 368340, display_name: 'CrossCode', app_type: 1, visible_in_game_list: true, subscribed_to: true };
function store(allApps: unknown[]) { return { m_bIsInitialized: true, allApps }; }
test('installed purchase-only games are not classified as licensed', () => {
    const data = captureLibrary(store([{ ...game, subscribed_to: false, owner_account_id: 39734274, local_per_client_data: { installed: true } }]), user, null);
    assert.equal(data.games[0].subscribed, false); assert.equal(data.games[0].installed, true);
    assert.equal(data.games[0].ownerAccountId, 39734274);
});
test('shared access stays distinguishable from direct ownership', () => {
    const data = captureLibrary(store([game, { ...game, appid: 570, owner_account_id: 123 }]), user, null);
    assert.equal(data.games[0].ownerAccountId, 0); assert.equal(data.games[1].ownerAccountId, 123);
    assert.ok(data.games.every(g => g.subscribed));
});
test('DLC, tools and non-Steam shortcuts are excluded from automatic imports', () => {
    const data = captureLibrary(store([game, ...[2,4,32,1073741824].map(app_type => ({ ...game, app_type }))]), user, null);
    assert.equal(data.games.length, 1);
});
test('missing access and cloud data remain unknown', () => {
    const data = captureLibrary(store([{ ...game, subscribed_to: undefined }]), user, null);
    assert.equal(data.games[0].subscribed, null); assert.equal(data.games[0].cloudEnabled, null);
});
test('complete synchronization includes hidden games so hiding cannot revoke a license', () => {
    const data = captureLibrary(store([{ ...game, visible_in_game_list: false }]), user, null);
    assert.equal(data.includesHiddenGames, true);
    assert.equal(data.games.length, 1);
    assert.equal(data.games[0].subscribed, true);
});
test('incomplete library and duplicate IDs cannot replace a valid snapshot', () => {
    assert.throws(() => captureLibrary({ ...store([game]), m_bIsInitialized: false }, user, null));
    assert.throws(() => captureLibrary(store([game,game]), user, null));
});
