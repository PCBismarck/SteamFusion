import assert from 'node:assert/strict';
import test from 'node:test';
import { decodeConfiguration } from '../frontend/configuration.ts';

const configuration = {
    environment: 'native',
    games: [{ appId: 368340, steamId: '76561198000000001', mode: 'auto', enabled: true, fixedEnvironment: 'native' }],
};

test('accepts the decoded object returned by the live Millennium FFI', () => {
    assert.deepEqual(decodeConfiguration(configuration), configuration);
});

test('also accepts a backend JSON string', () => {
    assert.deepEqual(decodeConfiguration(JSON.stringify(configuration)), configuration);
});

test('rejects malformed configuration before replacing live route rules', () => {
    for (const value of [null, {}, '{', { ...configuration, environment: '../native' },
        { ...configuration, games: [configuration.games[0], configuration.games[0]] },
        { ...configuration, games: [{ ...configuration.games[0], steamId: 76561198000000001 }] }]) {
        assert.throws(() => decodeConfiguration(value));
    }
});
