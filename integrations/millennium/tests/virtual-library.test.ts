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
