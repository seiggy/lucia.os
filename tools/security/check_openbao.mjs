import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash, generateKeyPairSync, randomUUID, sign } from 'node:crypto';
import { mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import https from 'node:https';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';

const { values } = parseArgs({ options: {
  image: { type: 'string' }, phase: { type: 'string' }, platform: { type: 'string', default: 'linux/amd64' },
} });
assert.match(values.image ?? '', /^(?:docker\.io\/)?openbao\/openbao:\d+\.\d+\.\d+@sha256:[a-f0-9]{64}$/,
  '--image must be an immutable official OpenBao release reference.');
assert.equal(values.phase, 'foundation', 'Only foundation is implemented; later phases must not report a partial pass as all.');
assert.ok(['linux/amd64', 'linux/arm64'].includes(values.platform), 'Use linux/amd64 or linux/arm64.');
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const id = `lucia-bao-check-${randomUUID().replaceAll('-', '')}`;
const temporary = mkdtempSync(path.join(tmpdir(), `${id}-`));
const state = path.join(temporary, 'state');
mkdirSync(state, { mode: 0o700 });
const user = typeof process.getuid === 'function' && process.getuid() > 0 ? `${process.getuid()}:${process.getgid()}` : '1000:1000';
const pins = readFileSync(path.join(root, 'src', 'Lucia.Homelab.Server', 'Platform', 'PlatformImages.cs'), 'utf8');
const step = pins.match(/new\("ca",[^]*?"smallstep\/step-ca", "([^"]+)",\s*"(sha256:[a-f0-9]{64})"/);
assert.ok(step, 'The fixture must use the existing pinned step-ca image.');
const stepImage = `smallstep/step-ca:${step[1]}@${step[2]}`;
const resticImage = 'restic/restic:0.18.1@sha256:39d9072fb5651c80d75c7a811612eb60b4c06b32ffe87c2e9f3c7222e1797e76';
let networkCreated = false;
let containerCreated = false;
let restoreCreated = false;
const volumesCreated = [];
let checkpoint = 'preflight';
const redactions = [];

function command(program, args, timeout = 120_000) {
  try {
    return execFileSync(program, args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], timeout }).trim();
  } catch (error) {
    // Never echo command bodies, initialization responses, tokens or shares.
    throw new Error(`${program} failed (exit ${error.status ?? 'unavailable'}); synthetic fixture stopped.`);
  }
}
const docker = (...args) => command('docker', args);
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
let port;
let ca;
let token;
function request(method, route, body, options = {}) {
  return new Promise((resolve, reject) => {
    const data = body === undefined ? undefined : Buffer.isBuffer(body) ? body : JSON.stringify(body);
    const { binary = false, ...connection } = options;
    const call = https.request({
      hostname: '127.0.0.1', port, servername: 'identity-gateway', ca,
      method, path: `/v1/${route}`, agent: false, timeout: route === 'sys/init' ? 60_000 : 5000,
      headers: { ...(token ? { 'X-Vault-Token': token } : {}),
        ...(data ? { 'Content-Type': Buffer.isBuffer(data) ? 'application/octet-stream' : 'application/json' } : {}) },
      ...connection,
    }, response => {
      const certificate = response.socket.getPeerCertificate().fingerprint256;
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.on('end', () => {
        const bytes = Buffer.concat(chunks);
        const text = bytes.toString('utf8');
        let json;
        try { json = binary ? null : text ? JSON.parse(text) : null; } catch { return reject(new Error('Invalid fixture API response.')); }
        resolve({ status: response.statusCode, json, certificate, bytes });
      });
    });
    call.on('timeout', () => call.destroy(Object.assign(new Error('Fixture API deadline expired.'), { code: 'ETIMEDOUT' })));
    call.on('error', reject);
    call.end(data);
  });
}
async function health(expected, container = id) {
  const deadline = Date.now() + 30_000;
  let last = 'no response';
  while (Date.now() < deadline) {
    const binding = JSON.parse(docker('inspect', container))[0].NetworkSettings.Ports['8200/tcp']?.[0];
    if (binding) port = Number(binding.HostPort);
    try {
      const result = await request('GET', 'sys/health');
      last = `HTTP ${result.status}, initialized=${result.json?.initialized}, sealed=${result.json?.sealed}`;
      if (result.status === expected) return result;
    } catch (error) {
      if (!['ECONNRESET', 'ECONNREFUSED', 'EPIPE', 'ETIMEDOUT'].includes(error.code)) throw error;
      last = error.code;
    }
    await delay(500);
  }
  const state = JSON.parse(docker('inspect', container))[0].State;
  const processes = spawnSync('docker', ['exec', container, '/bin/sh', '-c',
    'ps -o pid,ppid,args; cd /certificates; sha256sum -c ready.sha256'], { encoding: 'utf8', timeout: 10_000 });
  console.error(processes.stdout?.trim() || 'Synthetic process inspection unavailable.');
  const internal = spawnSync('docker', ['exec', container, 'bao', 'status', '-format=json'], { encoding: 'utf8', timeout: 10_000 });
  console.error(`Synthetic container-local status exit: ${internal.status}.`);
  const log = spawnSync('docker', ['logs', '--tail', '20', container], { encoding: 'utf8', timeout: 10_000 });
  let diagnostics = `${log.stdout ?? ''}\n${log.stderr ?? ''}`;
  for (const value of redactions) diagnostics = diagnostics.replaceAll(value, '[redacted]');
  diagnostics = diagnostics.split('\n').filter(line => /Error|permission denied|read-only|failed to|sealed|listener/i.test(line)).slice(-8);
  if (diagnostics.length) console.error(diagnostics.join('\n'));
  throw new Error(`Synthetic listener did not reach expected health status ${expected} (container ${state.Status}, exit ${state.ExitCode}; ${last}).`);
}
function certificates(renew = false) {
  const script = `${renew ? '' : `
step certificate create fixture-root /fixture/trust/lucia-root-ca.crt /fixture/synthetic-root.key --profile root-ca --no-password --insecure --not-after 48h
step certificate create other-root /fixture/untrusted.crt /fixture/untrusted.key --profile root-ca --no-password --insecure --not-after 48h
`}
step certificate create identity-gateway /fixture/certificates/identity.crt /fixture/certificates/identity.key --ca /fixture/trust/lucia-root-ca.crt --ca-key /fixture/synthetic-root.key --profile leaf --san identity-gateway --san localhost --san 127.0.0.1 --not-after 24h --no-password --insecure --force
cd /fixture/certificates
sha256sum identity.crt identity.key > ready.sha256
chown -R ${user} /fixture
chown ${user} /fixture-data /fixture-audit
`;
  docker('run', '--rm', '--network', id, '--label', `io.lucia.check=${id}`, '--user', '0:0',
    '--mount', `type=bind,source=${state},target=/fixture`,
    '--mount', `type=volume,source=${id}-data,target=/fixture-data`,
    '--mount', `type=volume,source=${id}-audit,target=/fixture-audit`,
    '--entrypoint', '/bin/sh', stepImage, '-ec', script);
}

try {
  docker('info', '--format', '{{.OSType}}');
  const stageArgs = [path.join(root, 'tools', 'identity', 'check_openbao.py'), '--stage-fixture', state];
  if (process.platform === 'win32') command('uv', ['run', '--no-project', 'python', ...stageArgs]);
  else command('python3', stageArgs);
  docker('network', 'create', '--label', `io.lucia.check=${id}`, id);
  networkCreated = true;
  for (const name of [`${id}-data`, `${id}-audit`]) {
    docker('volume', 'create', '--label', `io.lucia.check=${id}`, name);
    volumesCreated.push(name);
  }
  certificates();
  ca = readFileSync(path.join(state, 'trust', 'lucia-root-ca.crt'));
  const args = ['run', '-d', '--name', id, '--network', id, '--label', `io.lucia.check=${id}`,
    '--platform', values.platform, '--user', user, '--read-only', '--cap-drop', 'ALL',
    '--cpus', '2', '--memory', '1g',
    '--security-opt', 'no-new-privileges:true', '-p', '127.0.0.1::8200',
    '-e', 'BAO_ADDR=https://127.0.0.1:8200', '-e', 'BAO_CACERT=/trust/lucia-root-ca.crt',
    '-e', 'BAO_TLS_SERVER_NAME=identity-gateway', '--entrypoint', '/bin/sh'];
  for (const [source, target, readonly] of [
    ['openbao/config', '/bao/config', true], ['certificates', '/certificates', true], ['trust', '/trust', true],
  ]) args.push('--mount', `type=bind,source=${path.join(state, source)},target=${target}${readonly ? ',readonly' : ''}`);
  args.push('--mount', `type=volume,source=${id}-data,target=/bao/data`,
    '--mount', `type=volume,source=${id}-audit,target=/bao/audit`);
  docker(...args, values.image, '/bao/config/start-openbao.sh');
  containerCreated = true;
  port = Number(JSON.parse(docker('inspect', id))[0].NetworkSettings.Ports['8200/tcp'][0].HostPort);
  await health(501);
  checkpoint = 'uninitialized listener and trust rejection';
  docker('exec', id, '/bin/sh', '/bao/config/openbao-health.sh');
  await assert.rejects(request('GET', 'sys/health', undefined, { ca: readFileSync(path.join(state, 'untrusted.crt')) }),
    'Untrusted TLS must fail.');
  checkpoint = 'synthetic initialization';
  const initialized = await request('PUT', 'sys/init', { secret_shares: 3, secret_threshold: 2 });
  assert.equal(initialized.status, 200);
  const shares = initialized.json.keys;
  token = initialized.json.root_token;
  redactions.push(token, ...shares, ...(initialized.json.keys_base64 ?? []));
  assert.equal((await request('PUT', 'sys/init', { secret_shares: 3, secret_threshold: 2 })).status, 400,
    'Initialization must never replace existing Raft state.');
  checkpoint = 'manual unseal';
  assert.equal((await request('PUT', 'sys/unseal', { key: shares[0] })).json.sealed, true);
  assert.equal((await request('PUT', 'sys/unseal', { key: shares[1] })).json.sealed, false);
  await health(200);
  checkpoint = 'audit and scoped operator handoff';
  const originalRoot = token;
  const operatorPolicy = readFileSync(path.join(state, 'openbao', 'config', 'openbao-operator-policy.hcl'), 'utf8');
  const backupPolicy = readFileSync(path.join(state, 'openbao', 'config', 'openbao-backup-policy.hcl'), 'utf8');
  assert.equal((await request('PUT', 'sys/policies/acl/lucia-operator', { policy: operatorPolicy })).status, 204);
  const enabledAudit = await request('GET', 'sys/audit');
  assert.equal(enabledAudit.status, 200);
  assert.equal(enabledAudit.json.data['lucia-file/'].options.file_path, '/bao/audit/audit.json');
  const operator = await request('POST', 'auth/token/create-orphan', { policies: ['lucia-operator'], ttl: '1h' });
  assert.equal(operator.status, 200);
  let operatorToken = operator.json.auth.client_token;
  redactions.push(operatorToken);
  token = operatorToken;
  assert.equal((await request('POST', 'sys/mounts/lucia-secrets', { type: 'kv', options: { version: '2' } })).status, 204);
  assert.equal((await request('PUT', 'sys/policies/acl/lucia-openbao-backup', { policy: backupPolicy })).status, 204);
  assert.equal((await request('POST', 'sys/auth/lucia-operator', { type: 'userpass' })).status, 204);
  const operatorPassword = randomUUID();
  redactions.push(operatorPassword);
  assert.equal((await request('POST', 'auth/lucia-operator/users/operator', {
    password: operatorPassword, token_policies: ['lucia-operator'],
    token_ttl: '15m', token_max_ttl: '1h', token_explicit_max_ttl: '1h',
  })).status, 204);
  const operatorLogin = await request('POST', 'auth/lucia-operator/login/operator', { password: operatorPassword });
  assert.equal(operatorLogin.status, 200);
  operatorToken = operatorLogin.json.auth.client_token;
  redactions.push(operatorToken);
  assert.equal((await request('GET', 'sys/audit')).status, 200);
  assert.equal((await request('GET', 'sys/raw/fixture')).status, 403, 'The scoped operator cannot read raw storage.');
  assert.equal((await request('PUT', 'sys/policies/acl/unrelated', { policy: backupPolicy })).status, 403);
  token = originalRoot;
  assert.equal((await request('POST', 'auth/token/revoke-self')).status, 204);
  assert.equal((await request('GET', 'auth/token/lookup-self')).status, 403, 'The initial root must actually be revoked.');
  token = operatorToken;
  checkpoint = 'durable KV fixture';
  assert.equal((await request('POST', 'lucia-secrets/data/persist', { data: { value: 'synthetic-only' } })).status, 200);
  checkpoint = 'native JWT validation and scoped token exchange';
  const signing = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const otherSigning = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const issuer = 'https://auth.example.test/application/o/lucia-openbao/';
  const audience = 'synthetic-openbao-client';
  const now = Math.floor(Date.now() / 1000);
  const claims = { iss: issuer, aud: audience, sub: 'synthetic-operator', iat: now - 5, nbf: now - 5,
    exp: now + 300, lucia_openbao_operator: 'owner' };
  const jwt = (changes = {}, key = signing.privateKey) => {
    const unsigned = [Buffer.from(JSON.stringify({ alg: 'RS256', typ: 'JWT' })).toString('base64url'),
      Buffer.from(JSON.stringify({ ...claims, ...changes })).toString('base64url')].join('.');
    return unsigned + '.' + sign('RSA-SHA256', Buffer.from(unsigned), key).toString('base64url');
  };
  assert.equal((await request('POST', 'sys/auth/lucia-jwt-check', { type: 'jwt' })).status, 204);
  assert.equal((await request('POST', 'auth/lucia-jwt-check/config', {
    jwt_validation_pubkeys: [signing.publicKey.export({ type: 'spki', format: 'pem' })],
    bound_issuer: issuer, jwt_supported_algs: ['RS256'],
  })).status, 204);
  assert.equal((await request('PUT', 'sys/policies/acl/lucia-access-check', {
    policy: 'path "lucia-secrets/data/persist" { capabilities = ["read"] }',
  })).status, 204);
  assert.equal((await request('POST', 'auth/lucia-jwt-check/role/fixture', {
    role_type: 'jwt', user_claim: 'sub', bound_audiences: [audience], bound_subject: claims.sub,
    bound_claims_type: 'string', bound_claims: { lucia_openbao_operator: 'owner' },
    token_policies: ['lucia-access-check'], token_ttl: 900, token_max_ttl: 3600, token_explicit_max_ttl: 3600,
  })).status, 204);
  for (const changes of [{ iss: 'https://other.example.test/' }, { aud: 'wrong-client' }, { sub: 'other-user' },
    { lucia_openbao_operator: 'viewer' }, { lucia_openbao_operator: null }, { exp: now - 3600 }]) {
    const denied = await request('POST', 'auth/lucia-jwt-check/login', { role: 'fixture', jwt: jwt(changes) });
    assert.ok([400, 403].includes(denied.status), 'An invalid issuer, audience, subject, claim or expiry must fail.');
    assert.ok(!denied.json?.auth?.client_token);
  }
  assert.equal((await request('POST', 'auth/lucia-jwt-check/login', {
    role: 'fixture', jwt: jwt({}, otherSigning.privateKey),
  })).status, 400, 'A JWT signed by an unrelated key must fail.');
  const exchanged = await request('POST', 'auth/lucia-jwt-check/login', { role: 'fixture', jwt: jwt() });
  assert.equal(exchanged.status, 200);
  assert.ok(exchanged.json.auth.lease_duration > 0 && exchanged.json.auth.lease_duration <= 900);
  assert.deepEqual([...exchanged.json.auth.policies].sort(), ['default', 'lucia-access-check']);
  token = jwt();
  assert.equal((await request('GET', 'lucia-secrets/data/persist')).status, 403,
    'An external JWT is not itself an OpenBao API token.');
  token = exchanged.json.auth.client_token;
  redactions.push(token);
  assert.equal((await request('GET', 'lucia-secrets/data/persist')).status, 200);
  assert.equal((await request('GET', 'lucia-secrets/data/sibling')).status, 403);
  assert.equal((await request('GET', 'sys/raw/fixture')).status, 403);
  assert.equal((await request('PUT', 'sys/policies/acl/lucia-forbidden', { policy: backupPolicy })).status, 403);
  assert.equal((await request('POST', 'auth/token/revoke-self')).status, 204);
  token = undefined;
  assert.equal((await request('GET', 'lucia-secrets/data/persist')).status, 403);
  token = operatorToken;
  assert.equal((await request('DELETE', 'sys/auth/lucia-jwt-check')).status, 204);
  assert.equal((await request('DELETE', 'sys/policies/acl/lucia-access-check')).status, 204);
  checkpoint = 'short-lived backup authentication and authorization';
  assert.equal((await request('POST', 'sys/auth/openbao-backup', { type: 'approle' })).status, 204);
  assert.equal((await request('POST', 'auth/openbao-backup/role/snapshot', {
    token_policies: ['lucia-openbao-backup'], token_ttl: '15m', token_max_ttl: '1h', token_explicit_max_ttl: '1h',
    secret_id_ttl: '720h', secret_id_num_uses: 0,
  })).status, 204);
  const role = await request('GET', 'auth/openbao-backup/role/snapshot/role-id');
  const secret = await request('POST', 'auth/openbao-backup/role/snapshot/secret-id', {});
  assert.equal(role.status, 200);
  assert.equal(secret.status, 200);
  redactions.push(role.json.data.role_id, secret.json.data.secret_id);
  const login = await request('POST', 'auth/openbao-backup/login', {
    role_id: role.json.data.role_id, secret_id: secret.json.data.secret_id,
  });
  assert.equal(login.status, 200);
  assert.ok(login.json.auth.lease_duration > 0 && login.json.auth.lease_duration <= 900);
  token = login.json.auth.client_token;
  redactions.push(token);
  assert.equal((await request('GET', 'lucia-secrets/data/persist')).status, 403);
  assert.equal((await request('PUT', 'sys/policies/acl/lucia-forbidden', { policy: backupPolicy })).status, 403);
  const saved = await request('GET', 'sys/storage/raft/snapshot', undefined, { binary: true });
  assert.equal(saved.status, 200);
  assert.equal(saved.bytes.subarray(0, 2).toString('hex'), '1f8b');
  assert.equal((await request('POST', 'auth/token/revoke-self')).status, 204);
  assert.equal((await request('GET', 'sys/storage/raft/snapshot', undefined, { binary: true })).status, 403);
  token = operatorToken;
  checkpoint = 'enabled audit output failure';
  const audit = docker('exec', id, 'cat', '/bao/audit/audit.json');
  for (const sentinel of [originalRoot, operatorToken, operatorPassword, secret.json.data.secret_id, 'synthetic-only']) {
    assert.ok(!audit.includes(sentinel), 'Audit output must HMAC sensitive token and data fields.');
  }
  docker('exec', id, '/bin/sh', '-ec',
    'mv /bao/audit/audit.json /bao/audit/audit.good; ln -s /dev/full /bao/audit/audit.json; kill -HUP "$(pidof bao)"');
  await delay(1000);
  const blocked = await request('POST', 'lucia-secrets/data/persist', { data: { value: 'must-not-be-written' } });
  assert.equal(blocked.status, 500, 'An enabled audit destination that cannot write must fail closed.');
  docker('exec', id, '/bin/sh', '-ec',
    'rm /bao/audit/audit.json; mv /bao/audit/audit.good /bao/audit/audit.json; kill -HUP "$(pidof bao)"');
  await delay(1000);
  assert.equal((await request('GET', 'lucia-secrets/data/persist')).json.data.data.value, 'synthetic-only');
  checkpoint = 'renewed leaf reload';
  const before = (await request('GET', 'sys/health')).certificate;
  certificates(true);
  let reloaded = false;
  for (let attempt = 0; attempt < 30; attempt++) {
    if ((await request('GET', 'sys/health')).certificate !== before) { reloaded = true; break; }
    await delay(500);
  }
  assert.ok(reloaded, 'OpenBao must reload the verified renewed leaf without restarting or sealing.');
  checkpoint = 'restart and persisted secret';
  docker('restart', id);
  port = Number(JSON.parse(docker('inspect', id))[0].NetworkSettings.Ports['8200/tcp'][0].HostPort);
  await health(503);
  docker('exec', id, '/bin/sh', '/bao/config/openbao-health.sh');
  await request('PUT', 'sys/unseal', { key: shares[0] });
  await request('PUT', 'sys/unseal', { key: shares[1] });
  await health(200);
  const persisted = await request('GET', 'lucia-secrets/data/persist');
  assert.equal(persisted.status, 200);
  assert.equal(persisted.json.data.data.value, 'synthetic-only');
  checkpoint = 'restic encrypted round-trip and retention isolation';
  const snapshot = await request('GET', 'sys/storage/raft/snapshot', undefined, { binary: true });
  assert.equal(snapshot.status, 200);
  const backupDirectory = path.join(state, 'backup-fixture');
  mkdirSync(backupDirectory, { mode: 0o700 });
  writeFileSync(path.join(backupDirectory, 'openbao-raft.snap'), snapshot.bytes, { mode: 0o600 });
  const resticEnvironment = path.join(state, 'restic-fixture.env');
  const resticPassword = randomUUID();
  writeFileSync(resticEnvironment, `RESTIC_REPOSITORY=/fixture/restic-repo\nRESTIC_PASSWORD=${resticPassword}\nRESTIC_CACHE_DIR=/tmp/cache\n`,
    { mode: 0o600 });
  const restic = (...arguments_) => docker('run', '--rm', '--network', id, '--label', `io.lucia.check=${id}`,
    '--user', user, '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true',
    '--tmpfs', '/tmp', '--env-file', resticEnvironment,
    '--mount', `type=bind,source=${state},target=/fixture`, resticImage, ...arguments_);
  restic('init');
  restic('backup', '/fixture/backup-fixture', '--host', 'lucia-foundation', '--tag', 'lucia:foundation=openbao',
    '--time', '2020-01-01 00:00:00', '--quiet');
  restic('backup', '/fixture/backup-fixture', '--host', 'other-node', '--tag', 'lucia:stack=unrelated', '--quiet');
  restic('backup', '/fixture/backup-fixture', '--host', 'lucia-foundation', '--tag', 'lucia:foundation=openbao', '--quiet');
  restic('forget', '--tag', 'lucia:foundation=openbao', '--group-by', '', '--keep-daily', '1',
    '--keep-weekly', '0', '--keep-monthly', '0', '--prune', '--retry-lock', '30m');
  const listed = JSON.parse(restic('snapshots', '--json', '--no-lock'));
  assert.equal(listed.filter(item => item.tags.includes('lucia:stack=unrelated')).length, 1);
  const retained = listed.filter(item => item.tags.includes('lucia:foundation=openbao'));
  assert.equal(retained.length, 1);
  restic('restore', retained[0].id, '--target', '/fixture/restored', '--quiet');
  const restored = readFileSync(path.join(state, 'restored', 'fixture', 'backup-fixture', 'openbao-raft.snap'));
  assert.equal(createHash('sha256').update(restored).digest('hex'), createHash('sha256').update(snapshot.bytes).digest('hex'));
  checkpoint = 'production Raft-to-restic pipeline';
  writeFileSync(path.join(state, 'openbao', 'backup.json'), JSON.stringify({
    schema_version: 1, repository: path.join(state, 'restic-repo'), mount: state,
    keep_daily: 7, keep_weekly: 4, keep_monthly: 6, share_count: 3, share_threshold: 2,
    custody_confirmed: true, backup_recovery_confirmed: true,
  }), { mode: 0o600 });
  writeFileSync(path.join(state, 'openbao-settings.json'), JSON.stringify({
    schema_version: 1, bind_address: '127.0.0.1', port,
  }), { mode: 0o600 });
  for (const [name, value] of [
    ['openbao-restic-password', resticPassword], ['openbao-backup-role-id', role.json.data.role_id],
    ['openbao-backup-secret-id', secret.json.data.secret_id],
  ]) writeFileSync(path.join(state, 'secrets', name), value, { mode: 0o600 });
  const pipelineArgs = [path.join(root, 'tools', 'identity', 'check_openbao.py'),
    '--exercise-backup', state, '--restic-image', resticImage];
  const pipeline = JSON.parse(process.platform === 'win32'
    ? command('uv', ['run', '--no-project', 'python', ...pipelineArgs])
    : command('python3', pipelineArgs));
  assert.match(pipeline.snapshot, /^[a-f0-9]{64}$/);
  restic('restore', pipeline.snapshot, '--target', '/fixture/job-restored', '--quiet');
  const jobRoot = path.join(state, 'job-restored', 'fixture', 'openbao');
  const directories = readdirSync(jobRoot).filter(name => name.startsWith('.openbao-snapshot-'));
  assert.equal(directories.length, 1);
  const recoverySnapshot = readFileSync(path.join(jobRoot, directories[0], 'openbao-raft.snap'));
  assert.equal(recoverySnapshot.subarray(0, 2).toString('hex'), '1f8b');
  checkpoint = 'isolated actual snapshot restore';
  const restoreStart = Date.now();
  for (const suffix of ['restore-data', 'restore-audit']) {
    const name = `${id}-${suffix}`;
    docker('volume', 'create', '--label', `io.lucia.check=${id}`, name);
    volumesCreated.push(name);
  }
  docker('run', '--rm', '--network', id, '--label', `io.lucia.check=${id}`, '--user', '0:0',
    '--mount', `type=volume,source=${id}-restore-data,target=/restore-data`,
    '--mount', `type=volume,source=${id}-restore-audit,target=/restore-audit`,
    '--entrypoint', '/bin/sh', stepImage, '-ec', `chown ${user} /restore-data /restore-audit`);
  const restoreArgs = args.map(value => value === id ? `${id}-restore`
    : value.replace(`source=${id}-data,`, `source=${id}-restore-data,`).replace(`source=${id}-audit,`, `source=${id}-restore-audit,`));
  // Both containers share only fixture config/trust, never Raft or audit state.
  restoreArgs[restoreArgs.indexOf('--network') + 1] = id;
  docker(...restoreArgs, values.image, '/bao/config/start-openbao.sh');
  restoreCreated = true;
  await health(501, `${id}-restore`);
  const fresh = await request('PUT', 'sys/init', { secret_shares: 3, secret_threshold: 2 });
  assert.equal(fresh.status, 200);
  const restoreShares = fresh.json.keys;
  token = fresh.json.root_token;
  redactions.push(token, ...restoreShares, ...(fresh.json.keys_base64 ?? []));
  await request('PUT', 'sys/unseal', { key: restoreShares[0] });
  await request('PUT', 'sys/unseal', { key: restoreShares[1] });
  await health(200, `${id}-restore`);
  const wrongSeal = await request('POST', 'sys/storage/raft/snapshot', recoverySnapshot);
  assert.equal(wrongSeal.status, 400, 'A snapshot must not silently adopt a different seal.');
  assert.match(JSON.stringify(wrongSeal.json?.errors), /seal|decrypt|key|verify/i,
    'The ordinary restore must reject the different seal, not fail for an unrelated I/O reason.');
  const forceRestore = await request('POST', 'sys/storage/raft/snapshot-force', recoverySnapshot);
  let restoreErrors = JSON.stringify(forceRestore.json?.errors);
  for (const value of redactions) restoreErrors = restoreErrors?.replaceAll(value, '[redacted]');
  assert.equal(forceRestore.status, 204, restoreErrors);
  await health(503, `${id}-restore`);
  await request('PUT', 'sys/unseal', { key: shares[0] });
  await request('PUT', 'sys/unseal', { key: shares[1] });
  await health(200, `${id}-restore`);
  token = operatorToken;
  assert.equal((await request('GET', 'lucia-secrets/data/persist')).json.data.data.value, 'synthetic-only');
  assert.equal((await request('GET', 'sys/policies/acl/lucia-openbao-backup')).json.data.policy.trim(), backupPolicy.trim());
  const restoredLogin = await request('POST', 'auth/lucia-operator/login/operator', { password: operatorPassword });
  assert.equal(restoredLogin.status, 200, 'The independent operator login must survive restoration.');
  redactions.push(restoredLogin.json.auth.client_token);
  token = originalRoot;
  assert.equal((await request('GET', 'auth/token/lookup-self')).status, 403, 'Restoring the snapshot must not resurrect revoked root.');
  const restoreMilliseconds = Date.now() - restoreStart;
  docker('stop', '--time', '10', `${id}-restore`);
  docker('stop', '--time', '10', id);
  await assert.rejects(request('GET', 'sys/health'), 'A stopped instance must be unavailable.');
  console.log(JSON.stringify({ phase: values.phase, platform: values.platform, image: values.image,
    passed: ['private TLS', 'untrusted CA rejection', 'uninitialized', 'repeat-init refusal', 'manual unseal',
      'renewal reload', 'restart sealed', 'durable Raft', 'unavailable'],
    recoveryPassed: ['scoped operator', 'initial root revoked', 'short-lived snapshot-only auth', 'audit HMAC',
      'enabled audit failure closed', 'restic round-trip', 'tag-isolated retention', 'production backup pipeline',
      'original-quorum snapshot restore'],
    accessPassed: ['JWT signature and issuer', 'audience and subject bounds', 'owner claim bounds', 'expiry rejection',
      'JWT-to-token exchange', 'path-scoped token', 'external JWT rejected as API token', 'no unauthenticated secret reads'],
    restoreMilliseconds,
    configurationSha256: createHash('sha256').update(readFileSync(path.join(state, 'openbao', 'config', 'server.hcl'))).digest('hex') }));
} catch (error) {
  console.error(`Foundation qualification failed at: ${checkpoint}.`);
  throw error;
} finally {
  let cleanupError;
  for (const [created, args] of [[restoreCreated, ['rm', '-f', `${id}-restore`]], [containerCreated, ['rm', '-f', id]],
    ...volumesCreated.map(name => [true, ['volume', 'rm', name]]), [networkCreated, ['network', 'rm', id]]]) {
    if (!created) continue;
    try { docker(...args); } catch (error) { cleanupError = error; }
  }
  if (cleanupError) {
    console.error(`Cleanup failed for synthetic resource ${id}; private fixture retained at ${temporary}.`);
    throw cleanupError;
  }
  rmSync(temporary, { recursive: true, force: true });
}
