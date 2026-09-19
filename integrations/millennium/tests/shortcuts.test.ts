import { test } from 'node:test';
import assert from 'node:assert/strict';
import { addMissingShortcuts, removeGeneratedShortcuts, routeInShortcut } from '../frontend/shortcuts.ts';
const cli = 'F:\\SteamFusion\\SteamFusion.Cli.exe';
const mapping = (appId: number) => ({ appId, steamId: '76561198000000001', accountName: '小号 · Player', gameName: 'Game '+appId, mode: 'auto', enabled: true });
test('native-only library cleanup preserves legacy, unrelated and mismatched shortcuts', async () => {
    const removed:number[]=[];
    const rows=[
        {strShortcutExe:cli,strShortcutLaunchOptions:'launch 570 --steamfusion-library'},
        {strShortcutExe:cli,strShortcutLaunchOptions:'launch 620'},
        {strShortcutExe:'other.exe',strShortcutLaunchOptions:'launch 570 --steamfusion-library'},
        {strShortcutExe:cli,strShortcutLaunchOptions:'launch 570 --steamfusion-library extra'},
    ];
    const visible=rows.map((_,i)=>({appid:i+2500000000,app_type:1073741824}));
    visible.push({appid:570,app_type:1});
    assert.equal(await removeGeneratedShortcuts(visible,cli,{RemoveShortcut:async(id:number)=>removed.push(id)},
        async id=>rows[id-2500000000],()=>true),1);
    assert.deepEqual(removed,[2500000000]);
});
test('cleanup rechecks identity after awaiting shortcut details', async () => {
    let active=true;
    await removeGeneratedShortcuts([{appid:2500000000,app_type:1073741824}],cli,
        {RemoveShortcut:()=>assert.fail('must not mutate the new login')},
        async()=>{active=false;return {strShortcutExe:cli,strShortcutLaunchOptions:'launch 570 --steamfusion-library'};},()=>active);
});
test('shortcut identity uses executable and exact launch arguments, not display name', () => {
    assert.equal(routeInShortcut({strShortcutExe: '"'+cli+'"',strShortcutLaunchOptions:'launch 368340 --steamfusion-library'},cli),368340);
    assert.equal(routeInShortcut({strShortcutExe: cli,strShortcutLaunchOptions:'launch 368340'},cli),368340);
    assert.equal(routeInShortcut({strShortcutExe: 'evil.exe',strShortcutLaunchOptions:'launch 368340'},cli),null);
    assert.equal(routeInShortcut({strShortcutExe: cli,strShortcutLaunchOptions:'launch 368340; calc'},cli),null);
});
test('visible native games and existing routes are not duplicated, missing titles get structured fields', async () => {
    const calls: any[]=[];
    const apps = { AddShortcut:async (...args:any[])=>{calls.push(['add',...args]);return 2500000001;},
        SetShortcutExe:async (...args:any[])=>calls.push(['exe',...args]), SetShortcutStartDir:async (...args:any[])=>calls.push(['dir',...args]),
        SetShortcutLaunchOptions:async (...args:any[])=>calls.push(['args',...args]),
        SetShortcutName:async (...args:any[])=>calls.push(['name',...args]) };
    const added = await addMissingShortcuts([mapping(570),mapping(620),mapping(368340)],
        [{appid:570,app_type:1,visible_in_game_list:true},{appid:3000000000,app_type:1073741824}],cli,apps,
        async ()=>({strShortcutExe:cli,strShortcutLaunchOptions:'launch 620'}),()=>true);
    assert.equal(added,1); assert.equal(calls[0][1],'Game 368340 · 小号');
    assert.deepEqual(calls.at(-2),['args',2500000001,'launch 368340 --steamfusion-library']);
    assert.deepEqual(calls.at(-1),['name',2500000001,'Game 368340 · 小号']);
    assert.deepEqual(calls[2],['dir',2500000001,'"F:\\SteamFusion"']);
});
test('changing login stops library mutations', async () => {
    let active=true,added=0;
    const apps={AddShortcut:async()=>{added++;return 2500000001;},SetShortcutExe:async()=>{},SetShortcutStartDir:async()=>{},
        SetShortcutName:async()=>{}, SetShortcutLaunchOptions:async()=>{active=false;}};
    assert.equal(await addMissingShortcuts([mapping(570),mapping(620)],[],cli,apps,async()=>null,()=>active),1);
    assert.equal(added,1);
});
test('repairs only the executable-derived placeholder and preserves custom names', async () => {
    const renamed:any[]=[];
    const apps={SetShortcutName:async(...args:any[])=>renamed.push(args)};
    const added=await addMissingShortcuts([mapping(570),mapping(620)],
        [{appid:2500000001,app_type:1073741824},{appid:2500000002,app_type:1073741824}],cli,apps,
        async id=>({strShortcutExe:cli,strShortcutLaunchOptions:`launch ${id===2500000001?570:620} --steamfusion-library`,
            strDisplayName:id===2500000001?'SteamFusion.Cli':'My custom name'}),()=>true);
    assert.equal(added,0);assert.deepEqual(renamed,[[2500000001,'Game 570 · 小号']]);
});
test('a partially created shortcut is removed if its route cannot be configured', async () => {
    const removed:number[]=[];
    const apps={AddShortcut:async()=>2500000001,SetShortcutExe:async()=>{},SetShortcutStartDir:async()=>{},
        SetShortcutLaunchOptions:async()=>{throw new Error('write failed');},RemoveShortcut:async(id:number)=>removed.push(id)};
    await assert.rejects(addMissingShortcuts([mapping(570)],[],cli,apps,async()=>null,()=>true));
    assert.deepEqual(removed,[2500000001]);
});
