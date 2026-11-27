import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import {
  buildNotices,
  isPackageId,
  licenseVerdict,
  lockfileOnlyPackages,
  lockfilePackageIds,
  notInstalled,
  pnpmCommand,
  policyProblems,
  readNoticeTexts,
  readmeLicenseSection,
  registryLicense,
  serializeNotices,
} from './licenses.mjs';

const approved = (license) => ({ license, reason: 'reviewed', approvedBy: 'operator', approvedOn: '2026-11-27' });
const pkg = (name, version, license) => ({ name, version, license });
const ids = (...list) => new Set(list);

test('a single allowlisted license passes, case-insensitively', () => {
  assert.deepEqual(licenseVerdict('MIT'), { ok: true });
  assert.deepEqual(licenseVerdict('apache-2.0'), { ok: true });
});

test('OR passes when any branch passes, AND needs every branch', () => {
  assert.equal(licenseVerdict('(BSD-3-Clause OR GPL-2.0)').ok, true);
  assert.equal(licenseVerdict('(MIT OR Apache-2.0)').ok, true);
  assert.equal(licenseVerdict('Apache-2.0 AND MIT').ok, true);
  assert.equal(licenseVerdict('(MIT AND GPL-3.0)').ok, false);
  assert.equal(licenseVerdict('MIT AND (GPL-3.0 OR ISC)').ok, true);
  assert.equal(licenseVerdict('MIT OR GPL-3.0 AND LGPL-2.1').ok, true, 'AND binds tighter than OR');
  assert.equal(licenseVerdict('GPL-3.0 OR MIT AND LGPL-2.1').ok, false);
});

test('WITH fails unless its exception id is on the reviewed list', () => {
  assert.match(licenseVerdict('MIT WITH unreviewed-custom-terms').why, /not on the allowlist/);
  assert.equal(licenseVerdict('Apache-2.0 WITH LLVM-exception').ok, false, 'the default reviewed list is empty');
  assert.equal(licenseVerdict('(MIT WITH custom-terms) OR ISC').ok, true, 'another OR branch can still pass');
  assert.equal(licenseVerdict('MIT WITH').ok, false);
});

test('WITH passes a reviewed exception id on an allowlisted base license only', () => {
  const reviewed = new Set(['LLVM-exception']);
  assert.equal(licenseVerdict('Apache-2.0 WITH LLVM-exception', reviewed).ok, true);
  assert.equal(licenseVerdict('Apache-2.0 WITH other-exception', reviewed).ok, false);
  assert.equal(licenseVerdict('GPL-2.0-only WITH LLVM-exception', reviewed).ok, false);
});

test('GPL-only, missing, unknown and malformed licenses fail', () => {
  assert.match(licenseVerdict('GPL-3.0-only').why, /not on the allowlist/);
  assert.match(licenseVerdict(undefined).why, /missing/);
  assert.match(licenseVerdict('Unknown').why, /missing/);
  assert.match(licenseVerdict('(MIT OR').why, /unparseable/);
});

test('policy fails a GPL-only package and passes the allowlisted ones', () => {
  const problems = policyProblems([pkg('a', '1.0.0', 'MIT'), pkg('gpl-thing', '2.0.0', 'GPL-3.0')], {}, ids('a@1.0.0', 'gpl-thing@2.0.0'));
  assert.equal(problems.length, 1);
  assert.match(problems[0], /^gpl-thing@2\.0\.0: "GPL-3\.0" is not on the allowlist/);
});

test('policy fails a package with no license metadata', () => {
  const problems = policyProblems([pkg('bare', '1.0.0', 'Unknown')], {}, ids('bare@1.0.0'));
  assert.equal(problems.length, 1);
  assert.match(problems[0], /bare@1\.0\.0: missing or unknown license/);
});

test('an exception matches the exact version only', () => {
  const exceptions = { 'mpl-lib@1.2.3': approved('MPL-2.0') };
  const lock = ids('mpl-lib@1.2.3', 'mpl-lib@1.2.4');
  assert.deepEqual(policyProblems([pkg('mpl-lib', '1.2.3', 'MPL-2.0')], exceptions, lock), []);
  const other = policyProblems([pkg('mpl-lib', '1.2.3', 'MPL-2.0'), pkg('mpl-lib', '1.2.4', 'MPL-2.0')], exceptions, lock);
  assert.equal(other.length, 1);
  assert.match(other[0], /^mpl-lib@1\.2\.4: /);
});

test('an exception for a package missing from the lockfile is stale', () => {
  const exceptions = { 'gone@1.0.0': approved('MPL-2.0') };
  const problems = policyProblems([pkg('a', '1.0.0', 'MIT')], exceptions, ids('a@1.0.0'));
  assert.deepEqual(problems, ['stale exception gone@1.0.0: not in the resolved tree (remove it from license-exceptions.json)']);
});

test('an exception for another platform native is not stale while the lockfile resolves it', () => {
  const exceptions = { 'native-win32-x64@1.0.0': approved('MPL-2.0') };
  assert.deepEqual(policyProblems([], exceptions, ids('native-win32-x64@1.0.0')), []);
});

test('an incomplete exception and a license mismatch fail', () => {
  const problems = policyProblems(
    [pkg('x', '1.0.0', 'MPL-1.1')],
    { 'x@1.0.0': { license: 'MPL-2.0', reason: 'r', approvedBy: '', approvedOn: '27.11.2026' } },
    ids('x@1.0.0'),
  );
  assert.deepEqual(problems, [
    'exception x@1.0.0 has no "approvedBy"',
    'exception x@1.0.0 approvedOn "27.11.2026" is not YYYY-MM-DD',
    'x@1.0.0: exception records "MPL-2.0" but the package declares "MPL-1.1"',
  ]);
});

test('lockfilePackageIds reads every packages section key, quoted or scoped', () => {
  const lock = [
    "lockfileVersion: '9.0'",
    'packages:',
    "  '@scope/native-linux-x64@1.0.0':",
    '    resolution: {integrity: sha512-x}',
    '  plain@2.0.0:',
    'snapshots:',
    '  ignored@3.0.0:',
  ].join('\n');
  assert.deepEqual([...lockfilePackageIds(lock)], ['@scope/native-linux-x64@1.0.0', 'plain@2.0.0']);
});

test('lockfilePackageIds keeps reading packages across comments, indented comments and blank lines', () => {
  const lock = ['packages:', '  a@1.0.0: {resolution: {integrity: sha512-x}}', '# optional binaries', '  # platform: linux', '', '  b@2.0.0:', 'snapshots:', '  c@3.0.0:'].join('\n');
  assert.deepEqual([...lockfilePackageIds(lock)], ['a@1.0.0', 'b@2.0.0']);
});

test('lockfilePackageIds reads the same ids from a lockfile with a different indentation width', () => {
  const lock = readFileSync(new URL('../pnpm-lock.yaml', import.meta.url), 'utf8');
  // reindent only the first document's packages section by doubling its leading spaces
  const start = lock.indexOf('\npackages:\n') + 1;
  const end = lock.indexOf('\nsnapshots:\n');
  const reindented = lock.slice(0, start) + lock.slice(start, end).replace(/^( +)/gm, '$1$1') + lock.slice(end);
  assert.notEqual(reindented, lock);
  assert.deepEqual([...lockfilePackageIds(reindented)].sort(), [...lockfilePackageIds(lock)].sort());
});

test('lockfilePackageIds reads the same ids when a packages header is quoted', () => {
  const lock = readFileSync(new URL('../pnpm-lock.yaml', import.meta.url), 'utf8');
  const quoted = lock.replace('\npackages:\n', "\n'packages':\n");
  assert.notEqual(quoted, lock);
  assert.deepEqual([...lockfilePackageIds(quoted)].sort(), [...lockfilePackageIds(lock)].sort());
});

test('lockfilePackageIds reads keys whose value is an inline mapping', () => {
  const lock = [
    'packages:',
    "  '@s/native-win32-x64@1.0.0': {resolution: {integrity: sha512-x}, os: [win32], cpu: [x64]}",
    '  "dq@1.2.3": {resolution: {integrity: sha512-y}}',
    '  inline@4.0.0: {resolution: {integrity: sha512-z}}',
  ].join('\n');
  assert.deepEqual([...lockfilePackageIds(lock)], ['@s/native-win32-x64@1.0.0', 'dq@1.2.3', 'inline@4.0.0']);
});

test('notInstalled lists lockfile packages missing from the install, and registry metadata fills their license', () => {
  const missing = notInstalled(ids('a@1.0.0', '@s/native-win32@2.0.0', 'z@0.1.0'), [pkg('a', '1.0.0', 'MIT')]);
  assert.deepEqual(missing, ['@s/native-win32@2.0.0', 'z@0.1.0']);
  assert.deepEqual(lockfileOnlyPackages(missing, new Map([['@s/native-win32@2.0.0', 'MPL-2.0']])), [
    pkg('@s/native-win32', '2.0.0', 'MPL-2.0'),
    pkg('z', '0.1.0', undefined),
  ]);
});

test('removing the win32 lightningcss exception fails on Linux, where that package is not installed', () => {
  const exceptions = JSON.parse(readFileSync(new URL('../license-exceptions.json', import.meta.url), 'utf8')).packages;
  const win32 = 'lightningcss-win32-x64-msvc@1.33.0';
  const installed = [pkg('lightningcss', '1.33.0', 'MPL-2.0'), pkg('lightningcss-linux-x64-gnu', '1.33.0', 'MPL-2.0')];
  const lockIds = new Set(Object.keys(exceptions));
  // registry metadata for every lockfile package that is not installed here, as the exceptions record it
  const missing = notInstalled(lockIds, installed);
  const all = [...installed, ...lockfileOnlyPackages(missing, new Map(missing.map((id) => [id, exceptions[id].license])))];
  assert.ok(missing.includes(win32));
  assert.deepEqual(policyProblems(all, exceptions, lockIds), []);

  const { [win32]: _removed, ...withoutWin32 } = exceptions;
  const problems = policyProblems(all, withoutWin32, lockIds);
  assert.equal(problems.length, 1);
  assert.match(problems[0], /^lightningcss-win32-x64-msvc@1\.33\.0: "MPL-2\.0" is not on the allowlist/);
  assert.deepEqual(policyProblems(installed, withoutWin32, lockIds), [], 'judging installed packages alone misses it');
});

test('a lockfile-only package without registry license metadata fails', () => {
  const problems = policyProblems(lockfileOnlyPackages(['bare@1.0.0'], new Map()), {}, ids('bare@1.0.0'));
  assert.deepEqual(problems, ['bare@1.0.0: missing or unknown license (add an approved exact-version exception or drop the package)']);
});

test('readmeLicenseSection stops at the next heading of the same level', () => {
  const md = '# x\n\n## License\n\nMIT © Someone\n\n<!-- defs -->\n\n### Sub\n\nkept\n\n## Next\n\ndropped';
  assert.equal(readmeLicenseSection(md), '## License\n\nMIT © Someone\n\n### Sub\n\nkept');
  assert.equal(readmeLicenseSection('# x\n\nno section'), null);
});

const withTexts = (name, version, license, texts = [{ file: 'LICENSE', text: `${name} text` }]) => ({
  ...pkg(name, version, license),
  homepage: `https://example.com/${name}`,
  texts,
});

test('generate output is sorted and byte-identical regardless of input order', () => {
  const input = [
    withTexts('b', '1.0.0', 'MIT'),
    withTexts('@scope/a', '2.0.0', 'ISC', [{ file: 'NOTICE', text: 'n' }, { file: 'LICENSE', text: 'l' }]),
    withTexts('b', '0.9.0', 'MIT'),
  ];
  const first = buildNotices(input, {});
  const second = buildNotices([...input].reverse(), {});
  assert.deepEqual(first.problems, []);
  assert.equal(serializeNotices(first.notices), serializeNotices(second.notices));
  assert.deepEqual(first.notices.map((n) => `${n.name}@${n.version}`), ['@scope/a@2.0.0', 'b@0.9.0', 'b@1.0.0']);
  assert.deepEqual(first.notices[0].texts.map((t) => t.file), ['LICENSE', 'NOTICE']);
});

test('a package without text but with an allowlisted license is listed from its metadata', () => {
  const bare = { ...withTexts('bare', '1.0.0', '(MIT OR Apache-2.0)', []), author: { name: 'Jane Doe' }, repository: 'git+https://github.com/x/bare.git' };
  const { notices, problems } = buildNotices([bare], {});
  assert.deepEqual(problems, []);
  assert.deepEqual(notices[0], {
    name: 'bare',
    version: '1.0.0',
    license: '(MIT OR Apache-2.0)',
    homepage: 'https://example.com/bare',
    repository: 'https://github.com/x/bare',
    texts: [],
    missingText: 'No license text shipped in the package; licensed (MIT OR Apache-2.0) per package metadata',
    author: 'Jane Doe',
    spdx: ['https://spdx.org/licenses/MIT.html', 'https://spdx.org/licenses/Apache-2.0.html'],
  });
});

test('a package without text and without usable license metadata fails generation', () => {
  const { problems } = buildNotices([withTexts('bare', '1.0.0', 'Unknown', [])], {});
  assert.deepEqual(problems, ['bare@1.0.0: no license text in the package and missing or unknown license']);
});

test('an exception supplies the license and note, and unsafe URLs are dropped', () => {
  const entry = { ...withTexts('req', '0.1.2', 'Unknown'), homepage: 'javascript:alert(1)' };
  const { notices, problems } = buildNotices([entry], { 'req@0.1.2': approved('MIT') });
  assert.deepEqual(problems, []);
  assert.equal(notices[0].license, 'MIT');
  assert.equal(notices[0].note, 'reviewed');
  assert.equal(notices[0].homepage, undefined);
});

test('isPackageId accepts exact name@version ids, scoped and prerelease included', () => {
  for (const id of ['react@19.3.1', '@docusaurus/core@3.10.2', '@s/native-win32-x64@2.0.0-rc.1', 'a.b_c~d@0.0.0-alpha.1+build.5', 'JSONStream@1.3.5']) {
    assert.equal(isPackageId(id), true, id);
  }
});

test('isPackageId rejects anything that is not an exact name@version', () => {
  for (const id of ['foo@1.0.0 & calc', 'foo@^1', 'foo@1.0.0;rm', 'foo@1.0.0|x', 'git+https://github.com/a/b.git', 'git+ssh://git@github.com/a/b',
    'file:../pkg', '../pkg@1.0.0', './pkg@1.0.0', '/abs/pkg@1.0.0', 'C:\\pkg@1.0.0', '--registry=x@1.0.0', '-x@1.0.0', 'foo', 'foo@',
    'foo@1.0', 'foo@01.0.0', 'foo@1.0.0(react@19.3.1)', '@scope@1.0.0', '@a/b/c@1.0.0', 'foo@latest', undefined]) {
    assert.equal(isPackageId(id), false, String(id));
  }
});

test('registryLicense runs pnpm view without a shell and never looks up an invalid id', async () => {
  const calls = [];
  const exec = async (...args) => {
    calls.push(args);
    return { stdout: '"MIT"\n' };
  };
  const pnpm = ['/usr/bin/node', ['/opt/pnpm/pnpm.cjs']];
  assert.equal(await registryLicense('@s/native-win32@2.0.0', exec, pnpm), 'MIT');
  assert.equal(calls.length, 1);
  const [file, args, options] = calls[0];
  assert.equal(file, '/usr/bin/node');
  assert.deepEqual(args, ['/opt/pnpm/pnpm.cjs', 'view', '@s/native-win32@2.0.0', 'license', '--json']);
  assert.equal(options.shell, undefined);
  await assert.rejects(registryLicense('foo@1.0.0 & calc', exec, pnpm), /not an exact name@version; not looked up/);
  assert.equal(calls.length, 1, 'an invalid id is not looked up');
});

test('pnpmCommand runs a JS entry under this Node, a standalone pnpm directly, and needs pnpm', () => {
  assert.deepEqual(pnpmCommand('/opt/pnpm/bin/pnpm.cjs'), [process.execPath, ['/opt/pnpm/bin/pnpm.cjs']]);
  assert.deepEqual(pnpmCommand('/opt/pnpm/pnpm'), ['/opt/pnpm/pnpm', []]);
  assert.throws(() => pnpmCommand(''), /pnpm run licenses:check/);
  assert.throws(() => pnpmCommand('/usr/lib/node_modules/npm/bin/npm-cli.js'), /pnpm run licenses:check/);
});

test('readNoticeTexts includes nested license files and skips nested node_modules and symlinks', () => {
  const dir = mkdtempSync(join(tmpdir(), 'notices-'));
  try {
    writeFileSync(join(dir, 'LICENSE'), 'root MIT');
    mkdirSync(join(dir, 'src/vendor'), { recursive: true });
    writeFileSync(join(dir, 'src/vendor/LICENSE'), 'vendored MIT, Jane Doe');
    writeFileSync(join(dir, 'CopyrightNotice.txt'), 'Copyright Example Corp');
    writeFileSync(join(dir, 'src/vendor/viewer.js.LICENSE.txt'), 'bundled banner');
    writeFileSync(join(dir, 'src/vendor/licensed.js'), 'code, not a notice');
    mkdirSync(join(dir, 'node_modules/dep'), { recursive: true });
    writeFileSync(join(dir, 'node_modules/dep/LICENSE'), 'other package');
    symlinkSync(join(dir, 'node_modules/dep'), join(dir, 'linked'));
    assert.deepEqual(readNoticeTexts(dir), [
      { file: 'CopyrightNotice.txt', text: 'Copyright Example Corp' },
      { file: 'LICENSE', text: 'root MIT' },
      { file: 'src/vendor/LICENSE', text: 'vendored MIT, Jane Doe' },
      { file: 'src/vendor/viewer.js.LICENSE.txt', text: 'bundled banner' },
    ]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('readmeLicenseSection reads Setext headings underlined with - or =', () => {
  const dashes = '# murmur\n\nUsage\n-----\n\ncall it\n\nLicense (MIT)\n-------------\n\nCopyright (c) Gary Court, Jens Taylor\n\nThanks\n------\n\nbye';
  assert.equal(readmeLicenseSection(dashes), 'License (MIT)\n-------------\n\nCopyright (c) Gary Court, Jens Taylor');
  const equals = 'format\n======\n\nLicense\n=======\n\nMIT, Example Author';
  assert.equal(readmeLicenseSection(equals), 'License\n=======\n\nMIT, Example Author');
});
