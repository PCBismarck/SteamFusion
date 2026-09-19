import { test } from 'node:test';
import assert from 'node:assert/strict';
import { appIdFromFiber } from '../frontend/details.ts';

function branch(id: unknown) {
    const root: any = { stateNode: {} };
    root.stateNode.current = root;
    const component = { memoizedProps: { overview: { appid: id } }, return: root };
    return { root, node: { memoizedProps: { className: 'ActionSection' }, return: component } as any };
}
test('native detail reads the Steam app identity instead of the visible title', () => {
    assert.equal(appIdFromFiber(branch(368340).node), 368340);
    assert.equal(appIdFromFiber(branch('11427913603861184512').node), null);
});
test('a reused detail element follows the committed React branch after game navigation', () => {
    const old = branch(368340), next = branch(570);
    old.node.alternate = next.node;
    old.root.alternate = next.root;
    old.root.stateNode.current = next.root;
    assert.equal(appIdFromFiber(old.node), 570);
});
test('unmounted or unknown React trees cannot trigger a route', () => {
    assert.equal(appIdFromFiber(null), null);
    assert.equal(appIdFromFiber({ memoizedProps: { appid: 368340 } }), null);
    const stale = branch(368340);
    stale.root.stateNode.current = {};
    assert.equal(appIdFromFiber(stale.node), null);
    const cycle: any = {}; cycle.return = cycle;
    assert.equal(appIdFromFiber(cycle), null);
});
