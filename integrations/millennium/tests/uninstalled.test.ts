import test from 'node:test';
import assert from 'node:assert/strict';
import { installationIds, uninstalledGames, filterGames, artworkUrl, sortGames, librarySort } from '../frontend/uninstalled.ts';
const main = '76561198000000001', other = '76561198000000002';
const game = (appId: number, steamId = other) => ({ appId, steamId, enabled: true, mode: 'auto', gameName: `Game ${appId}` });
test('name and numeric ID sorts are deterministic and never mutate the catalog', () => {
    const games = [{...game(20), gameName:'Banana'}, {...game(100), gameName:'Apple'}, {...game(3), gameName:'Apple'}];
    const ids = (order) => sortGames(games, order).map(g => g.appId);
    assert.deepEqual(ids('name-asc'), [3,100,20]);
    assert.deepEqual(ids('name-desc'), [20,3,100]);
    assert.deepEqual(ids('appid-asc'), [3,20,100]);
    assert.deepEqual(ids('appid-desc'), [100,20,3]);
    assert.deepEqual(games.map(g=>g.appId), [20,100,3]);
    for (const value of [null, undefined, '', 'invalid', {}, 1]) assert.equal(librarySort(value), 'name-asc');
    assert.equal(librarySort('appid-desc'), 'appid-desc');
});
test('virtual library shows only enabled foreign-account games not installed locally', () => {
    const games = [game(10),game(20),game(30,main),{...game(40),enabled:false},game(50),game(60)];
    const local = new Set([20]);
    const result = uninstalledGames(games,main,main,local,[{appid:50,app_type:1,local_per_client_data:{installed:true}},{appid:60,app_type:1073741824,local_per_client_data:{installed:true}}]);
    assert.deepEqual(result.map(g=>g.appId),[10,60]); assert.deepEqual([...local],[20]);
});
test('changing Steam account hides the host-only virtual page', () => {
    for(const viewer of [null,other]) assert.deepEqual(uninstalledGames([game(10)],viewer,main,new Set(),[]),[]);
});
test('completed installation removes game, uninstall makes it available again', () => {
    assert.equal(uninstalledGames([game(10)],main,main,new Set([10]),[]).length,0);
    assert.equal(uninstalledGames([game(10)],main,main,new Set(),[]).length,1);
});
test('stale, incomplete and malformed installation reports fail closed', () => {
    const now=Date.now(), good={schemaVersion:1,capturedAt:new Date(now).toISOString(),complete:true,accounts:[main,other],installed:[10]};
    assert.deepEqual([...installationIds(JSON.stringify(good),now)],[10]);
    for(const bad of [null,{...good,complete:false},{...good,capturedAt:'invalid'},{...good,capturedAt:new Date(now-100000).toISOString()}, {...good,installed:['10']},{...good,installed:[-1]},{...good,accounts:['invalid']}]) assert.throws(()=>installationIds(bad,now));
});
test('virtual search accepts names and AppIDs without changing library data',()=>{
    const games=[{...game(10),gameName:'CrossCode'},game(20)];
    assert.deepEqual(filterGames(games,' cross '),[games[0]]); assert.deepEqual(filterGames(games,'20'),[games[1]]);assert.deepEqual(filterGames(games,'none'),[]);
});
test('artwork only uses the fixed public Steam CDN and valid numeric AppIDs',()=>{
    assert.equal(artworkUrl(10,'cover'),'https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/10/library_600x900.jpg');
    assert.throws(()=>artworkUrl(-1,'hero'));
});
