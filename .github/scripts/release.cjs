const fs = require('node:fs');
const crypto = require('node:crypto');

function versionParts(value) {
  if (!/^\d+\.\d+\.\d+\.\d+$/.test(value || '')) throw new Error('Expected a four-part version.');
  const parts = value.split('.').map(Number);
  if (parts.some(x => x > 65535) || parts[0] === 0 || parts.join('.') !== value) throw new Error('Invalid version components.');
  return parts;
}
function compareVersions(a, b) {
  const left = versionParts(a), right = versionParts(b);
  for (let i = 0; i < 4; i++) if (left[i] !== right[i]) return Math.sign(left[i] - right[i]);
  return 0;
}
function releaseMetadata(release) {
  const match = /<!-- kanban-store-release: (.*?) -->/.exec(release.body || '');
  if (!match) throw new Error(`Release ${release.tag_name} has no Store version mapping. Review it before proceeding.`);
  const metadata = JSON.parse(match[1]);
  versionParts(metadata.productVersion); versionParts(metadata.storePackageVersion);
  if (!/^[a-f0-9]{40}$/.test(metadata.commit || '') || `v${metadata.productVersion}` !== release.tag_name) throw new Error('Invalid existing release metadata.');
  return metadata;
}
function validateReleaseState(candidate, releases, tagExists) {
  versionParts(candidate.productVersion);
  if (versionParts(candidate.storePackageVersion)[3] !== 0) throw new Error('Store revision must be zero.');
  if (!/^[a-f0-9]{40}$/.test(candidate.commit || '')) throw new Error('Invalid source commit.');
  const tag = `v${candidate.productVersion}`;
  const existing = releases.find(x => x.tag_name === tag);
  if (existing) {
    const metadata = releaseMetadata(existing);
    if (!existing.draft || existing.immutable || metadata.commit !== candidate.commit || existing.target_commitish !== candidate.commit ||
        metadata.storePackageVersion !== candidate.storePackageVersion || existing.assets.length !== 0 || tagExists) {
      throw new Error('Version already used. Existing releases and tags will not be overwritten.');
    }
    return existing; // A repeated successful preparation reuses the unchanged draft.
  }
  if (tagExists) throw new Error('Version tag already exists.');
  for (const release of releases.filter(x => /^v\d+\.\d+\.\d+\.\d+$/.test(x.tag_name))) {
    const previous = releaseMetadata(release);
    if (compareVersions(candidate.productVersion, previous.productVersion) <= 0 ||
        compareVersions(candidate.storePackageVersion, previous.storePackageVersion) <= 0) {
      throw new Error('Product and Store versions must both exceed every prepared release.');
    }
  }
  return null;
}
function requireSuccessfulRun(runs, sha, name) {
  const latest = runs.filter(x => x.head_sha === sha && x.head_branch === 'main' &&
    ['push', 'workflow_dispatch'].includes(x.event)).sort((a, b) => b.id - a.id)[0];
  if (!latest || latest.status !== 'completed' || latest.conclusion !== 'success') throw new Error(`${name} must pass on the exact main commit first.`);
}
function requireCompleteReport(report, candidate, checks) {
  if (report.schemaVersion !== 1 || report.channel !== 'Store' || report.dirty !== false ||
      report.commit !== candidate.commit || report.productVersion !== candidate.productVersion ||
      report.storePackageVersion !== candidate.storePackageVersion || !/^[A-Fa-f0-9]{64}$/.test(report.sha256 || '') ||
      report.bundle !== `KanbanTasker-Store-${candidate.storePackageVersion}.msixbundle` ||
      checks.length !== 2 || checks.map(x => x.architecture).sort().join(',') !== 'arm64,x64' ||
      checks.some(x => x.success !== true || x.productVersion !== candidate.productVersion || x.packageVersion !== candidate.storePackageVersion || x.binaries < 1)) {
    throw new Error('Incomplete, dirty or mismatched Store build evidence.');
  }
}
async function main() {
  const mode = process.argv[2];
  if (!['check', 'draft'].includes(mode)) throw new Error('Usage: release.cjs check|draft');
  const env = process.env;
  if (env.GITHUB_REF !== 'refs/heads/main' || env.GITHUB_EVENT_NAME !== 'workflow_dispatch') throw new Error('Release preparation must be started manually on main.');
  const candidate = { productVersion: env.PRODUCT_VERSION, storePackageVersion: env.STORE_PACKAGE_VERSION, commit: env.GITHUB_SHA };
  versionParts(candidate.productVersion); versionParts(candidate.storePackageVersion);
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(env.GITHUB_REPOSITORY || '')) throw new Error('Invalid repository.');
  if (!env.GITHUB_TOKEN) throw new Error('Missing workflow token.');
  const base = `https://api.github.com/repos/${env.GITHUB_REPOSITORY}`;
  async function api(path, method = 'GET', body, allow404 = false) {
    const response = await fetch(base + path, { method,
      headers: { Authorization: `Bearer ${env.GITHUB_TOKEN}`, Accept: 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28', 'Content-Type': 'application/json' },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }), signal: AbortSignal.timeout(30000) });
    if (allow404 && response.status === 404) return null;
    if (!response.ok) throw new Error(`GitHub ${method} ${path} failed (${response.status}).`);
    return response.status === 204 ? null : response.json();
  }
  const repository = await api('');
  if (repository.default_branch !== 'main') throw new Error('This workflow requires main as the default branch.');
  const head = await api('/git/ref/heads/main');
  if (head.object.sha !== candidate.commit) throw new Error('main changed. Start a new preparation on its current commit.');
  for (const workflow of repository.private ? ['ci.yml'] : ['ci.yml', 'codeql.yml']) {
    const runs = await api(`/actions/workflows/${workflow}/runs?head_sha=${candidate.commit}&per_page=100`);
    requireSuccessfulRun(runs.workflow_runs, candidate.commit, workflow);
  }
  const releases = [];
  for (let page = 1; ; page++) {
    const batch = await api(`/releases?per_page=100&page=${page}`);
    releases.push(...batch);
    if (batch.length < 100) break;
    if (page === 100) throw new Error('Release history is too large to validate safely.');
  }
  const tag = `v${candidate.productVersion}`;
  const tagExists = await api(`/git/ref/tags/${tag}`, 'GET', undefined, true);
  const existing = validateReleaseState(candidate, releases, !!tagExists);
  const output = (key, value) => { if (env.GITHUB_OUTPUT) fs.appendFileSync(env.GITHUB_OUTPUT, `${key}=${value}\n`); };
  output('product_version', candidate.productVersion); output('store_version', candidate.storePackageVersion);
  if (!/^[A-Z0-9]{12}$/.test(env.STORE_ID || '')) throw new Error('Invalid Store product ID.');
  output('store_id', env.STORE_ID);
  output('already_prepared', existing ? 'true' : 'false');
  if (existing) { console.log(`Existing unchanged draft: ${existing.html_url}`); return; }
  if (mode === 'check') { console.log(`Eligible: ${tag}, Store ${candidate.storePackageVersion}, commit ${candidate.commit}`); return; }
  const report = JSON.parse(fs.readFileSync('build/store/submission/build-report.json', 'utf8').replace(/^\uFEFF/, ''));
  const checks = JSON.parse(fs.readFileSync('build/store/submission/verification.json', 'utf8').replace(/^\uFEFF/, ''));
  requireCompleteReport(report, candidate, checks);
  const bundle = fs.readFileSync(`build/store/submission/${report.bundle}`);
  if (crypto.createHash('sha256').update(bundle).digest('hex') !== report.sha256.toLowerCase()) throw new Error('Downloaded submission bundle checksum mismatch.');
  // Only a draft is created. No EXE/MSIX assets, publication, mutable tag or Store submission.
  const body = `<!-- kanban-store-release: ${JSON.stringify(candidate)} -->\n` +
    `Product version: **${candidate.productVersion}** · Store package: **${candidate.storePackageVersion}**\n\n` +
    `Source commit: ${candidate.commit}\n\n` +
    `Install and update through [Microsoft Store](https://apps.microsoft.com/detail/${env.STORE_ID}).\n\n` +
    `Maintainer: upload the verified Store bundle from [this workflow run](https://github.com/${env.GITHUB_REPOSITORY}/actions/runs/${env.GITHUB_RUN_ID}) to Partner Center. Complete STORE.md acceptance and publish this draft only after Store availability has been verified.\n\n` +
    `Direct GitHub installers and the GitHub updater are not yet offered.\n\n`;
  const release = await api('/releases', 'POST', { tag_name: tag, target_commitish: candidate.commit,
    name: `Kanban Tasker SoloSync ${candidate.productVersion}`, body, draft: true, prerelease: false, generate_release_notes: true });
  console.log(`Draft prepared: ${release.html_url}`);
  if (env.GITHUB_STEP_SUMMARY) fs.appendFileSync(env.GITHUB_STEP_SUMMARY,
    `Prepared [release draft](${release.html_url}). Nothing has been published or submitted to Microsoft.\n`);
}
module.exports = { versionParts, compareVersions, validateReleaseState, requireSuccessfulRun, requireCompleteReport };
if (require.main === module) main().catch(error => { console.error(error.message); process.exitCode = 1; });
