const { test } = require('node:test');
const assert = require('node:assert/strict');
const { versionParts, compareVersions, validateReleaseState, requireSuccessfulRun, requireCompleteReport } = require('./release.cjs');
const candidate = { productVersion: '2.4.1.8', storePackageVersion: '2.4.1.0', commit: 'a'.repeat(40) };
const draft = { tag_name: 'v2.4.1.8', target_commitish: candidate.commit, draft: true, assets: [], body: `<!-- kanban-store-release: ${JSON.stringify(candidate)} -->` };
test('version validation rejects missing, oversized and ambiguous components', () => {
  for (const value of ['', '1.2.3', '0.2.3.4', '1.2.3.65536', '01.2.3.4', 'v1.2.3.4', '1.2.3.4\n']) assert.throws(() => versionParts(value));
  assert.equal(compareVersions('2.4.1.10', '2.4.1.9'), 1);
});
test('new preparation requires a fresh tag and Store revision zero', () => {
  assert.equal(validateReleaseState(candidate, [], false), null);
  assert.throws(() => validateReleaseState(candidate, [], true));
  assert.throws(() => validateReleaseState({ ...candidate, storePackageVersion: '2.4.1.8' }, [], false));
});
test('repeat preparation reuses only the same unchanged unpublished draft', () => {
  assert.equal(validateReleaseState(candidate, [draft], false), draft);
  for (const altered of [{ ...draft, draft: false }, { ...draft, immutable: true }, { ...draft, assets: [{}] },
    { ...draft, target_commitish: 'main' }, { ...draft, body: '' }, { ...draft, body: draft.body.replace('a'.repeat(40), 'b'.repeat(40)) }]) {
    assert.throws(() => validateReleaseState(candidate, [altered], false));
  }
  assert.throws(() => validateReleaseState(candidate, [draft], true));
});
test('both product and Store package versions must advance', () => {
  const previous = { ...draft, tag_name: 'v2.4.1.7', body: `<!-- kanban-store-release: ${JSON.stringify({ ...candidate, productVersion: '2.4.1.7' })} -->` };
  assert.throws(() => validateReleaseState(candidate, [previous], false));
  assert.equal(validateReleaseState({ ...candidate, storePackageVersion: '2.4.2.0' }, [previous], false), null);
});
test('failed, skipped, stale, PR or pending checks cannot authorize release', () => {
  const good = { id: 1, head_sha: candidate.commit, head_branch: 'main', event: 'push', status: 'completed', conclusion: 'success' };
  requireSuccessfulRun([good], candidate.commit, 'CI');
  for (const altered of [{ ...good, head_sha: 'b'.repeat(40) }, { ...good, event: 'pull_request' },
    { ...good, head_branch: 'feature' }, { ...good, status: 'in_progress' }, { ...good, conclusion: 'skipped' }, { ...good, conclusion: 'failure' }]) {
    assert.throws(() => requireSuccessfulRun([altered], candidate.commit, 'CI'));
  }
  assert.throws(() => requireSuccessfulRun([good, { ...good, id: 2, conclusion: 'failure' }], candidate.commit, 'CI'));
  assert.throws(() => requireSuccessfulRun([], candidate.commit, 'CI'));
});
test('incomplete, wrong-version or dirty packages cannot become a release draft', () => {
  const report = { schemaVersion: 1, channel: 'Store', dirty: false, ...candidate, sha256: 'a'.repeat(64), bundle: 'KanbanTasker-Store-2.4.1.0.msixbundle' };
  const checks = ['x64', 'arm64'].map(architecture => ({ architecture, success: true, productVersion: candidate.productVersion, packageVersion: candidate.storePackageVersion, binaries: 1 }));
  requireCompleteReport(report, candidate, checks);
  for (const altered of [{ ...report, dirty: true }, { ...report, commit: 'b'.repeat(40) }, { ...report, sha256: '' }, { ...report, channel: 'Local' }]) {
    assert.throws(() => requireCompleteReport(altered, candidate, checks));
  }
  assert.throws(() => requireCompleteReport(report, candidate, checks.slice(1)));
  assert.throws(() => requireCompleteReport(report, candidate, [checks[0], checks[0]]));
  assert.throws(() => requireCompleteReport(report, candidate, checks.map(x => ({ ...x, success: false }))));
});
