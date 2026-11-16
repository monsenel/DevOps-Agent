import { spawn } from 'node:child_process';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import net from 'node:net';
import { deflateSync, inflateSync } from 'node:zlib';
import { chromium } from 'playwright';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const output = path.join(root, 'docs', 'screenshots');
const temp = await mkdtemp(path.join(tmpdir(), 'devops-agent-readme-'));
const database = path.join(temp, 'demo.db');
const keys = path.join(temp, 'keys');
const password = 'readme-demo-only-password';
const dotnetEnv = Object.fromEntries(['PATH', 'HOME', 'DOTNET_ROOT', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES']
  .filter(name => process.env[name])
  .map(name => [name, process.env[name]]));
const pngSignature = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
const crcTable = Uint32Array.from({ length: 256 }, (_, index) => {
  let value = index;
  for (let bit = 0; bit < 8; bit++) value = value & 1 ? 0xedb88320 ^ (value >>> 1) : value >>> 1;
  return value >>> 0;
});

function crc32(bytes) {
  let value = 0xffffffff;
  for (const byte of bytes) value = crcTable[(value ^ byte) & 0xff] ^ (value >>> 8);
  return (value ^ 0xffffffff) >>> 0;
}

function pngChunk(type, data) {
  const typeBytes = Buffer.from(type, 'ascii');
  const checksumInput = Buffer.concat([typeBytes, data]);
  const chunk = Buffer.allocUnsafe(12 + data.length);
  chunk.writeUInt32BE(data.length, 0);
  typeBytes.copy(chunk, 4);
  data.copy(chunk, 8);
  chunk.writeUInt32BE(crc32(checksumInput), 8 + data.length);
  return chunk;
}

async function optimizePng(file) {
  const input = await readFile(file);
  if (!input.subarray(0, 8).equals(pngSignature)) throw new Error(`Invalid PNG file: ${file}`);
  const chunks = [];
  const imageData = [];
  let offset = 8;
  while (offset < input.length) {
    const length = input.readUInt32BE(offset);
    const type = input.toString('ascii', offset + 4, offset + 8);
    const data = input.subarray(offset + 8, offset + 8 + length);
    chunks.push({ type, data });
    if (type === 'IDAT') imageData.push(data);
    offset += length + 12;
  }
  const encoded = [];
  let replacedImageData = false;
  for (const { type, data } of chunks) {
    if (type === 'IDAT') {
      if (!replacedImageData) {
        encoded.push(pngChunk('IDAT', deflateSync(inflateSync(Buffer.concat(imageData)), { level: 9 })));
        replacedImageData = true;
      }
      continue;
    }
    encoded.push(pngChunk(type, data));
  }
  if (!replacedImageData) throw new Error(`PNG has no image data: ${file}`);
  await writeFile(file, Buffer.concat([pngSignature, ...encoded]));
}

async function freeLoopbackPort() {
  const server = net.createServer();
  await new Promise((resolve, reject) => server.once('error', reject).listen(0, '127.0.0.1', resolve));
  const { port } = server.address();
  await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  return port;
}

async function stop(child) {
  if (!child || child.exitCode !== null) return;
  child.kill('SIGTERM');
  await new Promise(resolve => {
    const timer = setTimeout(() => { child.kill('SIGKILL'); resolve(); }, 10000);
    child.once('exit', () => { clearTimeout(timer); resolve(); });
  });
}

let app;
let browser;
try {
  await mkdir(output, { recursive: true });
  const seed = spawn('dotnet', ['run', '--project', 'tools/DevOpsAgent.DocsDemo', '--', database], {
    cwd: root, env: dotnetEnv, stdio: 'inherit',
  });
  const seedExitCode = await new Promise((resolve, reject) => {
    seed.once('error', reject);
    seed.once('exit', resolve);
  });
  if (seedExitCode !== 0) throw new Error(`Synthetic demo data seeding failed with code ${seedExitCode}.`);

  const port = await freeLoopbackPort();
  const baseUrl = `http://127.0.0.1:${port}`;
  app = spawn('dotnet', ['run', '--project', 'src/DevOpsAgent.Web', '--no-launch-profile'], {
    cwd: root,
    stdio: 'ignore',
    env: {
      ...dotnetEnv,
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: baseUrl,
      ADMIN_USER: 'demo',
      ADMIN_PASSWORD: password,
      PersistenceOptions__ConnectionString: `Data Source=${database}`,
      PersistenceOptions__DataProtectionKeysDirectory: keys,
      Ssh__ManagedKnownHostsFile: path.join(temp, 'known_hosts'),
    },
  });

  let ready = false;
  for (let attempt = 0; attempt < 90; attempt++) {
    if (app.exitCode !== null) throw new Error(`Application exited with code ${app.exitCode}.`);
    try {
      const response = await fetch(`${baseUrl}/healthz`);
      if (response.ok) { ready = true; break; }
    } catch { /* Wait for Kestrel to start. */ }
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  if (!ready) throw new Error('The local app did not become ready within 90 seconds.');

  browser = await chromium.launch({ headless: true, executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE || undefined });
  const context = await browser.newContext({
    viewport: { width: 1600, height: 1000 },
    deviceScaleFactor: 1,
    locale: 'en-US',
    timezoneId: 'UTC',
    colorScheme: 'light',
    reducedMotion: 'reduce',
  });
  await context.addInitScript(() => localStorage.setItem('dva-theme', 'light'));
  const page = await context.newPage();
  page.on('pageerror', error => console.error(`Browser page error: ${error.message}`));
  await page.goto(`${baseUrl}/login`, { waitUntil: 'networkidle' });
  await page.getByLabel('Username').fill('demo');
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL(`${baseUrl}/`);
  await page.getByRole('heading', { name: /servers, .*need attention/ }).waitFor();
  await page.evaluate(() => document.fonts.ready);
  await page.addStyleTag({ content: '*,:before,:after{animation:none!important;transition:none!important;scroll-behavior:auto!important;caret-color:transparent!important}' });
  await page.screenshot({ path: path.join(output, 'overview.png'), fullPage: true, animations: 'disabled' });

  await page.goto(`${baseUrl}/topology`, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: 'Infrastructure map' }).waitFor();
  await page.locator('.topology-node-list button').first().waitFor();
  await page.evaluate(() => document.fonts.ready);
  await page.evaluate(() => document.activeElement?.blur());
  await page.screenshot({ path: path.join(output, 'topology.png'), fullPage: true, animations: 'disabled' });

  await page.goto(`${baseUrl}/alerts`, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: 'Alerts', exact: true }).first().waitFor();
  await page.getByText('SSH authentication failed').first().waitFor();
  await page.evaluate(() => document.fonts.ready);
  await page.evaluate(() => document.activeElement?.blur());
  await page.screenshot({ path: path.join(output, 'alerts.png'), fullPage: true, animations: 'disabled' });

  await page.goto(`${baseUrl}/updates`, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: 'Updates', exact: true }).waitFor();
  await page.getByText('openssl', { exact: true }).waitFor();
  await page.evaluate(() => document.fonts.ready);
  await page.evaluate(() => document.activeElement?.blur());
  await page.screenshot({ path: path.join(output, 'updates.png'), fullPage: true, animations: 'disabled' });
  await context.close();
  for (const name of ['overview.png', 'topology.png', 'alerts.png', 'updates.png']) {
    await optimizePng(path.join(output, name));
  }
  console.log(`Captured overview.png, topology.png, alerts.png, and updates.png in ${path.relative(root, output)}.`);
} finally {
  if (browser) await browser.close();
  await stop(app);
  await rm(temp, { recursive: true, force: true });
}
