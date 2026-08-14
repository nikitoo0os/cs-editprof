'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const { findDemoUrl, findMatchMetadata, isValveReplayUrl } = require('./resolve.cjs');

const replayUrl = 'http://replay271.valve.net/730/003830292815003255677_1716728332.dem.bz2';

test('extracts a Valve replay URL from current round stats', () => {
  assert.equal(findDemoUrl([{ roundstatsall: [{ map: replayUrl }] }]), replayUrl);
});

test('supports the legacy round-stats field', () => {
  assert.equal(findDemoUrl([{ roundstats_legacy: { map: replayUrl } }]), replayUrl);
});

test('extracts match time and final score for the history picker', () => {
  const result = findMatchMetadata([{
    matchtime: 1_765_000_000,
    roundstatsall: [
      { map: replayUrl, team_scores: [6, 6] },
      { map: null, team_scores: [13, 8] }
    ]
  }]);

  assert.deepEqual(result, {
    demoUrl: replayUrl,
    playedAtUnix: 1_765_000_000,
    score: '13:8'
  });
});

test('rejects untrusted and malformed replay URLs', () => {
  assert.equal(isValveReplayUrl('https://example.com/730/demo.dem.bz2'), false);
  assert.equal(isValveReplayUrl('not-a-url'), false);
  assert.equal(findDemoUrl([{ roundstatsall: [{ map: 'https://example.com/demo.dem' }] }]), null);
});
