'use strict';

const fs = require('node:fs');
const path = require('node:path');
const GlobalOffensive = require('globaloffensive');
const SteamUser = require('steam-user');

class BotError extends Error {
  constructor(code, message, exitCode = 1) {
    super(message);
    this.code = code;
    this.exitCode = exitCode;
  }
}

const commandArguments = process.argv.slice(2);
const allowMissing = commandArguments.includes('--allow-missing');
const codes = commandArguments
  .filter((value) => value !== '--allow-missing')
  .map((value) => value.trim())
  .filter(Boolean);
const repositoryRoot = path.resolve(__dirname, '..', '..');
const defaultTokenFile = path.join(repositoryRoot, 'artifacts', 'steam-gc-bot', 'refresh-token.txt');
const tokenFile = path.resolve(process.env.CS2_STEAM_BOT_REFRESH_TOKEN_FILE || defaultTokenFile);
const dataDirectory = path.resolve(
  process.env.CS2_STEAM_BOT_DATA_DIRECTORY ||
  path.join(repositoryRoot, 'artifacts', 'steam-gc-bot', 'data'));
const requestTimeoutMs = Math.max(
  10_000,
  Number.parseInt(process.env.CS2_STEAM_BOT_REQUEST_TIMEOUT_MS || '30000', 10) || 30_000);

function readRefreshToken() {
  const fromEnvironment = (process.env.CS2_STEAM_BOT_REFRESH_TOKEN || '').trim();
  if (fromEnvironment) return fromEnvironment;
  if (fs.existsSync(tokenFile)) return fs.readFileSync(tokenFile, 'utf8').trim();
  throw new BotError(
    'STEAM_BOT_NOT_CONFIGURED',
    'Steam bot refresh token is missing. Run npm run steam-bot:auth first.',
    2);
}

function findDemoUrl(matches) {
  for (const match of matches || []) {
    const roundStats = [
      ...(match.roundstatsall || []),
      match.roundstats_legacy
    ].filter(Boolean);
    for (const stats of roundStats) {
      const candidate = typeof stats.map === 'string' ? stats.map.trim() : '';
      if (isValveReplayUrl(candidate)) return candidate;
    }
  }
  return null;
}

function findMatchMetadata(matches) {
  for (const match of matches || []) {
    const roundStats = [
      ...(match.roundstatsall || []),
      match.roundstats_legacy
    ].filter(Boolean);
    const statsWithReplay = roundStats.find((stats) => isValveReplayUrl(stats.map));
    if (!statsWithReplay) continue;
    const finalStats = roundStats.at(-1) || statsWithReplay;
    const scores = Array.isArray(finalStats.team_scores) ? finalStats.team_scores : [];
    return {
      demoUrl: statsWithReplay.map.trim(),
      playedAtUnix: Number(match.matchtime) || null,
      score: scores.length >= 2 ? `${scores[0]}:${scores[1]}` : null
    };
  }
  return null;
}

function isValveReplayUrl(value) {
  try {
    const parsed = new URL(value);
    return (parsed.protocol === 'http:' || parsed.protocol === 'https:') &&
      parsed.hostname.toLowerCase().endsWith('.valve.net') &&
      parsed.pathname.startsWith('/730/') &&
      parsed.pathname.endsWith('.dem.bz2');
  } catch {
    return false;
  }
}

function waitForGc(client, gc) {
  return new Promise((resolve, reject) => {
    let loggedOn = false;
    const timeout = setTimeout(() => {
      cleanup();
      reject(new BotError(
        loggedOn ? 'STEAM_BOT_GC_UNAVAILABLE' : 'STEAM_BOT_AUTH_FAILED',
        loggedOn
          ? 'Steam bot connected, but the CS2 Game Coordinator did not answer.'
          : 'Steam bot could not log in before the timeout.',
        loggedOn ? 4 : 3));
    }, 90_000);

    const cleanup = () => {
      clearTimeout(timeout);
      client.removeListener('error', onSteamError);
      gc.removeListener('error', onGcError);
      gc.removeListener('connectedToGC', onConnectedToGc);
    };
    const fail = (error, code) => {
      cleanup();
      reject(new BotError(code, error.message || String(error), code === 'STEAM_BOT_AUTH_FAILED' ? 3 : 4));
    };
    const onSteamError = (error) => fail(error, 'STEAM_BOT_AUTH_FAILED');
    const onGcError = (error) => fail(error, 'STEAM_BOT_GC_UNAVAILABLE');
    const onConnectedToGc = () => {
      cleanup();
      resolve();
    };

    client.on('error', onSteamError);
    gc.on('error', onGcError);
    gc.on('connectedToGC', onConnectedToGc);
    client.once('loggedOn', async () => {
      loggedOn = true;
      try {
        await client.requestFreeLicense([730]);
      } catch (error) {
        if (process.env.CS2_STEAM_BOT_DEBUG === '1') {
          process.stderr.write(`Free CS2 license request failed: ${error.message}\n`);
        }
      }
      client.gamesPlayed([730]);
    });

    try {
      client.logOn({
        refreshToken: readRefreshToken(),
        machineName: 'CSHighlighter GC Bot'
      });
    } catch (error) {
      cleanup();
      reject(error instanceof BotError
        ? error
        : new BotError('STEAM_BOT_AUTH_FAILED', error.message || String(error), 3));
    }
  });
}

function requestMatch(gc, code) {
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      gc.removeListener('matchList', onMatchList);
      reject(new BotError('STEAM_BOT_GC_UNAVAILABLE', `Steam GC timed out for ${code}.`, 4));
    }, requestTimeoutMs);

    const onMatchList = (matches) => {
      clearTimeout(timeout);
      const metadata = findMatchMetadata(matches);
      if (metadata) {
        resolve({ code, ...metadata });
        return;
      }
      if (!matches || matches.length === 0) {
        reject(new BotError('MATCH_NOT_FOUND', `Steam GC did not find ${code}.`, 5));
        return;
      }
      reject(new BotError('DEMO_URL_NOT_FOUND', `Steam GC returned ${code} without a replay URL.`, 5));
    };

    gc.once('matchList', onMatchList);
    try {
      gc.requestGame(code);
    } catch (error) {
      clearTimeout(timeout);
      gc.removeListener('matchList', onMatchList);
      reject(new BotError('INVALID_MATCH_CODE', error.message, 5));
    }
  });
}

async function main() {
  if (codes.length === 0) {
    throw new BotError('NO_MATCH_CODES', 'Pass at least one CS2 share code.', 2);
  }

  const client = new SteamUser({
    autoRelogin: false,
    dataDirectory,
    enablePicsCache: false,
    renewRefreshTokens: false
  });
  const gc = new GlobalOffensive(client);
  if (process.env.CS2_STEAM_BOT_DEBUG === '1') {
    client.on('debug', (message) => process.stderr.write(`[steam] ${message}\n`));
    gc.on('debug', (message) => process.stderr.write(`[gc] ${message}\n`));
  }

  try {
    await waitForGc(client, gc);
    const results = [];
    for (const code of codes) {
      try {
        results.push(await requestMatch(gc, code));
      } catch (error) {
        if (!allowMissing) throw error;
        const normalized = error instanceof BotError
          ? error
          : new BotError('STEAM_BOT_FAILED', error.message || String(error), 1);
        results.push({
          code,
          demoUrl: null,
          playedAtUnix: null,
          score: null,
          errorCode: normalized.code
        });
      }
    }
    return results;
  } finally {
    try {
      client.gamesPlayed([]);
      client.logOff();
    } catch {
      // The connection may already be closed after a Steam-side error.
    }
  }
}

if (require.main === module) {
  main().then((results) => {
    process.stdout.write(`${JSON.stringify(results)}\n`, () => process.exit(0));
  }).catch((error) => {
    const normalized = error instanceof BotError
      ? error
      : new BotError('STEAM_BOT_FAILED', error.message || String(error), 1);
    process.stderr.write(`${JSON.stringify({ code: normalized.code, message: normalized.message })}\n`, () => {
      process.exit(normalized.exitCode);
    });
  });
}

module.exports = { findDemoUrl, findMatchMetadata, isValveReplayUrl };
