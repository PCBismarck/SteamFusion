import { JSDOM } from 'jsdom';
import assert from 'node:assert/strict';
import { installVirtualLibrary } from '../frontend/virtual-library.ts';
import test from 'node:test';
test('virtual page interactions preserve native library and guard account changes', async () => {
const dom=new JSDOM('<html><head></head><body><aside id="sidebar"><div class="native-nav"></div><div>native games</div></aside></body></html>',{url:'https://steamloopback.host'});
const doc=dom.window.document;
globalThis.ResizeObserver=class {observe(){}disconnect(){}};
doc.querySelector('#sidebar').getBoundingClientRect=()=>({left:0,right:250,top:80,bottom:780,width:250,height:700});
Object.defineProperty(doc.documentElement,'clientWidth',{value:1200});
const game=(id,name)=>({appId:id,steamId:'76561198000000002',enabled:true,mode:'auto',gameName:name,accountName:'Secondary'});
let model={enabled:true,user:'76561198000000001',games:[game(10,'CrossCode'),game(20,'<img src=x onerror=alert(1)>')],loading:false};
let requested=[];
const view=installVirtualLibrary({document:()=>doc,navigationClass:()=> 'native-nav',model:()=>model,refreshData:()=>{},download:async id=>requested.push(id)});
view.refresh(); assert.equal(doc.querySelectorAll('.sfvl-nav').length,1);assert.ok(doc.querySelector('.sfvl-page').hidden);
doc.querySelector('.sfvl-nav').click();assert.equal(doc.querySelectorAll('.sfvl-card').length,2);
assert.equal(doc.querySelectorAll('.sfvl-card img[src="x"]').length,0);
const input=doc.querySelector('input');input.value='cross';input.dispatchEvent(new dom.window.Event('input'));assert.equal(doc.querySelectorAll('.sfvl-card').length,1);
doc.querySelector('.sfvl-card').click();assert.equal(doc.querySelector('.sfvl-detail h2').textContent,'CrossCode');
doc.querySelector('.sfvl-primary').click();await new Promise(r=>setTimeout(r,0));assert.deepEqual(requested,[10]);
model={...model,games:[]};view.refresh();assert.equal(doc.querySelectorAll('.sfvl-card').length,0);assert.equal(doc.querySelectorAll('.sfvl-detail').length,0);
model={...model,games:[game(10,'CrossCode')]};view.refresh();doc.querySelector('.sfvl-card').click();
model={...model,enabled:false,user:null};doc.querySelector('.sfvl-primary').click();await new Promise(r=>setTimeout(r,0));assert.deepEqual(requested,[10]);
view.refresh();assert.equal(doc.querySelectorAll('.sfvl-page,.sfvl-nav').length,0);
model={enabled:true,user:'76561198000000001',games:[game(10,'CrossCode')],loading:false};view.refresh();doc.querySelector('.sfvl-nav').click();doc.dispatchEvent(new dom.window.KeyboardEvent('keydown',{key:'Escape'}));assert.ok(doc.querySelector('.sfvl-page').hidden);
view.dispose();assert.equal(doc.querySelectorAll('.sfvl-nav,.sfvl-page,style').length,0);assert.equal(doc.querySelector('#sidebar').textContent,'native games');
dom.window.close();
});

test('scrolling loads additional batches without replacing cards, focus or details; search covers the full list', async () => {
    const dom = new JSDOM('<body><aside id="sidebar"><div class="native-nav"></div></aside></body>');
    const doc = dom.window.document;
    globalThis.ResizeObserver = class { observe() {} disconnect() {} };
    doc.querySelector('#sidebar').getBoundingClientRect = () => ({ left:0, right:250, top:80, bottom:780, width:250, height:700 });
    let model = { enabled:true, user:'76561198000000001', loading:false,
        games:Array.from({length:135}, (_,i) => ({appId:i+1, steamId:'76561198000000002', enabled:true, mode:'auto', gameName:`Game ${i+1}`})) };
    const view = installVirtualLibrary({document:()=>doc, navigationClass:()=> 'native-nav', model:()=>model, refreshData:()=>{}, download:async()=>{}});
    const tick = () => new Promise(resolve => setTimeout(resolve, 45));
    try {
        view.open();
        const scroll = doc.querySelector('.sfvl-scroll'), grid = doc.querySelector('.sfvl-grid');
        Object.defineProperty(scroll, 'clientHeight', {value:500});
        Object.defineProperty(scroll, 'scrollHeight', {get:()=>grid.children.length * 50});
        assert.equal(grid.children.length,60);
        const first = grid.firstElementChild; first.click();
        const detail = doc.querySelector('.sfvl-detail'), focused = doc.activeElement;
        scroll.scrollTop = 2500;
        for(let i=0;i<10;i++) scroll.dispatchEvent(new dom.window.Event('scroll'));
        await tick();
        assert.equal(grid.children.length,120); assert.equal(grid.firstElementChild,first);
        assert.equal(doc.querySelector('.sfvl-detail'),detail); assert.equal(doc.activeElement,focused); assert.equal(scroll.scrollTop,2500);
        scroll.scrollTop = 5500; scroll.dispatchEvent(new dom.window.Event('scroll')); await tick();
        assert.equal(grid.children.length,135); assert.match(doc.querySelector('.sfvl-more').textContent,/已显示全部 135/);
        scroll.dispatchEvent(new dom.window.Event('scroll')); await tick(); assert.equal(grid.children.length,135);
        const search=doc.querySelector('input'); search.value='135'; search.dispatchEvent(new dom.window.Event('input'));
        assert.equal(grid.children.length,1); assert.equal(grid.firstElementChild.title,'Game 135'); assert.equal(scroll.scrollTop,0);
        search.value=''; search.dispatchEvent(new dom.window.Event('input')); assert.equal(grid.children.length,60);
        scroll.scrollTop=2500; scroll.dispatchEvent(new dom.window.Event('scroll'));
        search.value='missing'; search.dispatchEvent(new dom.window.Event('input')); await tick();
        assert.equal(doc.querySelectorAll('.sfvl-card').length,0);
        search.value=''; search.dispatchEvent(new dom.window.Event('input')); scroll.scrollTop=2500; scroll.dispatchEvent(new dom.window.Event('scroll'));
        model={...model, enabled:false, user:null}; await tick(); assert.equal(grid.children.length,60);
        view.dispose(); await tick(); assert.equal(doc.querySelectorAll('.sfvl-page,.sfvl-nav').length,0);
    } finally { view.dispose(); dom.window.close(); }
});

test('sort covers all pages and search results, survives reload and stays scoped to the viewer', async () => {
    const dom = new JSDOM('<body><aside id="sidebar"><div class="native-nav"></div></aside></body>', {url:'https://steamloopback.host'});
    const doc = dom.window.document;
    globalThis.ResizeObserver = class { observe() {} disconnect() {} };
    doc.querySelector('#sidebar').getBoundingClientRect = () => ({left:0,right:250,top:80,bottom:780,width:250,height:700});
    let model = {enabled:true, user:'76561198000000001', loading:false,
        games:Array.from({length:135},(_,i)=>({appId:i+1, steamId:'76561198000000002', enabled:true, mode:'auto', gameName:`Game ${i+1}`}))};
    const options = {document:()=>doc, navigationClass:()=> 'native-nav', model:()=>model, refreshData:()=>{}, download:async()=>{}};
    let view = installVirtualLibrary(options);
    const titles = () => [...doc.querySelectorAll('.sfvl-card')].map(card=>card.title);
    const select = value => {const node=doc.querySelector('select');node.value=value;node.dispatchEvent(new dom.window.Event('change'));};
    try {
        view.open(); select('appid-desc');
        assert.deepEqual(titles(),Array.from({length:60},(_,i)=>`Game ${135-i}`));
        const scroll=doc.querySelector('.sfvl-scroll');
        Object.defineProperty(scroll,'clientHeight',{value:500});
        Object.defineProperty(scroll,'scrollHeight',{get:()=>doc.querySelectorAll('.sfvl-card').length*50});
        scroll.scrollTop=2500;scroll.dispatchEvent(new dom.window.Event('scroll'));
        await new Promise(resolve=>setTimeout(resolve,45));
        assert.deepEqual(titles(),Array.from({length:120},(_,i)=>`Game ${135-i}`));
        const search=doc.querySelector('input');search.value='13';search.dispatchEvent(new dom.window.Event('input'));
        assert.deepEqual(titles(),['Game 135','Game 134','Game 133','Game 132','Game 131','Game 130','Game 113','Game 13']);
        model={...model,games:model.games.filter(g=>g.appId!==134)};view.refresh();
        assert.equal(doc.querySelector('select').value,'appid-desc');assert.equal(titles().includes('Game 134'),false);
        view.dispose(); view=installVirtualLibrary(options); view.open();
        assert.equal(doc.querySelector('select').value,'appid-desc');assert.equal(titles()[0],'Game 135');
        model={...model,user:'76561198000000003'};view.open();
        assert.equal(doc.querySelector('select').value,'name-asc');
        model={...model,user:'76561198000000001'};view.open();
        assert.equal(doc.querySelector('select').value,'appid-desc');
        select('alphabetical-asc');
        assert.equal(titles()[0],'Game 1');assert.equal(titles()[1],'Game 10');
        view.dispose();view=installVirtualLibrary(options);view.open();
        assert.equal(doc.querySelector('select').value,'alphabetical-asc');
        select('alphabetical-desc');assert.equal(titles()[0],'Game 99');
        view.dispose();view=installVirtualLibrary(options);view.open();
        assert.equal(doc.querySelector('select').value,'alphabetical-desc');
        assert.equal(model.games[0].appId,1);
    } finally {view.dispose();dom.window.close();}
});

test('invalid saved sort and unavailable storage fall back without breaking the page', () => {
    const dom=new JSDOM('<body><aside id="sidebar"><div class="native-nav"></div></aside></body>',{url:'https://steamloopback.host'});
    const doc=dom.window.document;
    globalThis.ResizeObserver=class {observe(){}disconnect(){}};
    doc.querySelector('#sidebar').getBoundingClientRect=()=>({left:0,right:250,top:80,bottom:780,width:250,height:700});
    const model={enabled:true,user:'76561198000000001',loading:false,games:[{appId:3,steamId:'76561198000000002',enabled:true,mode:'auto',gameName:'Example'}]};
    const options={document:()=>doc,navigationClass:()=> 'native-nav',model:()=>model,refreshData:()=>{},download:async()=>{}};
    dom.window.localStorage.setItem(`steamfusion.uninstalled.sort.v1:${model.user}`,'bad-mode');
    let view=installVirtualLibrary(options);
    try {
        view.open();assert.equal(doc.querySelector('select').value,'name-asc');view.dispose();
        Object.defineProperty(dom.window,'localStorage',{get(){throw new Error('Storage denied');}});
        view=installVirtualLibrary(options);view.open();
        const select=doc.querySelector('select');select.value='appid-desc';select.dispatchEvent(new dom.window.Event('change'));
        assert.equal(select.value,'appid-desc');assert.equal(doc.querySelectorAll('.sfvl-card').length,1);
    } finally {view.dispose();dom.window.close();}
});

test('collapsible sidebar browses the full library and shares search, sorting and safe detail navigation', () => {
    const dom = new JSDOM('<body><aside id="sidebar"><div class="native-nav"></div><button id="native-game">Native game</button></aside></body>', {url:'https://steamloopback.host'});
    const doc = dom.window.document;
    globalThis.ResizeObserver = class {observe(){} disconnect(){}};
    doc.querySelector('#sidebar').getBoundingClientRect = () => ({left:0,right:250,top:80,bottom:780,width:250,height:700});
    const native = doc.querySelector('#native-game'); let nativeClicks = 0; native.addEventListener('click',()=>nativeClicks++);
    let model = {enabled:true,user:'76561198000000001',loading:false,games:Array.from({length:135},(_,i)=>({appId:i+1,steamId:'76561198000000002',enabled:true,mode:'auto',gameName:`Game ${i+1}`}))};
    let downloads=0;
    const options={document:()=>doc,navigationClass:()=> 'native-nav',model:()=>model,refreshData:()=>{},download:async()=>{downloads++;}};
    let view=installVirtualLibrary(options);
    try {
        view.refresh();assert.equal(doc.querySelector('.sfvl-side-body').hidden,true);
        doc.querySelector('.sfvl-side-toggle').click();assert.equal(doc.querySelector('.sfvl-side-body').hidden,false);
        assert.equal(doc.querySelectorAll('.sfvl-side-game').length,135);
        const last=doc.querySelector('.sfvl-side-game[data-appid="135"]');last.focus();last.click();
        assert.equal(doc.querySelector('.sfvl-detail h2').textContent,'Game 135');assert.equal(doc.activeElement,last);
        assert.equal(last.getAttribute('aria-current'),'page');assert.equal(downloads,0);
        last.dispatchEvent(new dom.window.Event('pointerdown',{bubbles:true}));assert.equal(doc.querySelector('.sfvl-page').hidden,false);
        doc.querySelector('.sfvl-side-list').scrollTop=200;
        const input=doc.querySelector('.sfvl-side-search');input.value='135';input.dispatchEvent(new dom.window.Event('input'));
        assert.equal(doc.querySelector('.sfvl-controls input').value,'135');
        assert.equal(doc.querySelector('.sfvl-side-list').scrollTop,0);
        assert.equal(doc.querySelectorAll('.sfvl-side-game').length,1);assert.equal(doc.querySelectorAll('.sfvl-card').length,1);
        const gridSearch=doc.querySelector('.sfvl-controls input');gridSearch.value='';gridSearch.dispatchEvent(new dom.window.Event('input'));
        assert.equal(input.value,'');assert.equal(doc.querySelectorAll('.sfvl-side-game').length,135);
        const sort=doc.querySelector('select');sort.value='appid-desc';sort.dispatchEvent(new dom.window.Event('change'));
        assert.equal(doc.querySelector('.sfvl-side-game').dataset.appid,'135');
        const first=doc.querySelector('.sfvl-side-game');first.focus();first.dispatchEvent(new dom.window.KeyboardEvent('keydown',{key:'ArrowDown',bubbles:true}));
        assert.equal(doc.activeElement.dataset.appid,'134');assert.equal(doc.querySelector('.sfvl-detail h2').textContent,'Game 134');
        const stale=doc.activeElement;model={...model,games:model.games.filter(g=>g.appId!==134)};view.refresh();
        assert.equal(doc.querySelector('.sfvl-side-game[data-appid="134"]'),null);assert.equal(doc.querySelector('.sfvl-detail'),null);
        stale.click();assert.equal(doc.querySelector('.sfvl-detail'),null);
        native.dispatchEvent(new dom.window.Event('pointerdown',{bubbles:true}));native.click();
        assert.equal(doc.querySelector('.sfvl-page').hidden,true);assert.equal(nativeClicks,1);
        view.dispose();view=installVirtualLibrary(options);view.refresh();assert.equal(doc.querySelector('.sfvl-side-body').hidden,false);
        const old=doc.querySelector('.sfvl-side-game');model={...model,user:'76561198000000003'};old.click();assert.equal(doc.querySelector('.sfvl-page').hidden,true);
        view.refresh();assert.equal(doc.querySelector('.sfvl-side-body').hidden,true);
        model={...model,enabled:false};view.refresh();assert.equal(doc.querySelector('.sfvl-side-group'),null);
        assert.equal(doc.querySelector('#native-game'),native);assert.equal(downloads,0);
    } finally {view.dispose();dom.window.close();}
});
