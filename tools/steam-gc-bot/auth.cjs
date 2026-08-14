'use strict';

const fs = require('node:fs');
const path = require('node:path');
const QRCode = require('qrcode');
const qrcode = require('qrcode-terminal');
const { EAuthTokenPlatformType, LoginSession } = require('steam-session');

const repositoryRoot = path.resolve(__dirname, '..', '..');
const outputArgument = process.argv.indexOf('--output');
const outputPath = path.resolve(
  outputArgument >= 0 && process.argv[outputArgument + 1]
    ? process.argv[outputArgument + 1]
    : path.join(repositoryRoot, 'artifacts', 'steam-gc-bot', 'refresh-token.txt'));
const qrPath = path.join(repositoryRoot, 'artifacts', 'steam-gc-bot', 'login-qr.png');

async function authenticate() {
  const session = new LoginSession(EAuthTokenPlatformType.SteamClient);
  session.loginTimeout = 180_000;

  const authenticated = new Promise((resolve, reject) => {
    session.once('authenticated', resolve);
    session.once('timeout', () => reject(new Error('Steam QR login timed out.')));
    session.once('error', reject);
  });

  const response = await session.startWithQR();
  fs.mkdirSync(path.dirname(qrPath), { recursive: true });
  await QRCode.toFile(qrPath, response.qrChallengeUrl, {
    errorCorrectionLevel: 'M',
    margin: 3,
    width: 640
  });
  process.stdout.write('\nОтсканируй QR-код приложением Steam и подтверди вход бота:\n\n');
  qrcode.generate(response.qrChallengeUrl, { small: true });
  process.stdout.write(`\nQR_IMAGE=${qrPath}\n`);
  process.stdout.write(`STEAM_LOGIN_URL=${response.qrChallengeUrl}\n`);
  process.stdout.write('\nОжидаю подтверждение Steam Guard...\n');
  await authenticated;

  if (!session.refreshToken) {
    throw new Error('Steam authenticated without returning a refresh token.');
  }

  fs.mkdirSync(path.dirname(outputPath), { recursive: true });
  fs.writeFileSync(outputPath, `${session.refreshToken}\n`, { encoding: 'utf8', mode: 0o600 });
  process.stdout.write(`\nГотово. Refresh token сохранён в ${outputPath}\n`);
  process.stdout.write('Не добавляй этот файл в git и не отправляй его другим людям.\n');
}

authenticate().catch((error) => {
  process.stderr.write(`Не удалось авторизовать Steam-бота: ${error.message}\n`);
  process.exitCode = 1;
}).finally(() => {
  try {
    if (fs.existsSync(qrPath)) fs.unlinkSync(qrPath);
  } catch {
    // A stale QR contains no reusable account credential and will expire shortly.
  }
});
