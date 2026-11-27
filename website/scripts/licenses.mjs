// License gate and third-party notices for the website's dependency tree.
//   node scripts/licenses.mjs check     policy over every package in the lockfile (prod + dev, all platforms), plus
//                                       notice evidence for prod; packages not installed here are judged from registry metadata
//   node scripts/licenses.mjs generate  writes src/data/third-party-notices.generated.json for the production tree
import { execFile, spawnSync } from 'node:child_process';
import { mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadAll } from 'js-yaml';
import { fromMarkdown } from 'mdast-util-from-markdown';
import { promisify } from 'node:util';

const siteRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const exceptionsPath = join(siteRoot, 'license-exceptions.json');
const lockfilePath = join(siteRoot, 'pnpm-lock.yaml');
const outputPath = join(siteRoot, 'src/data/third-party-notices.generated.json');

export const ALLOWED = new Set(
  ['MIT', 'MIT-0', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', '0BSD', 'CC0-1.0', 'CC-BY-4.0',
    'BlueOak-1.0.0', 'Python-2.0', 'Unlicense'].map((id) => id.toLowerCase()),
);
// Reviewed SPDX exception ids a WITH may carry. Empty: nothing in the tree uses WITH, so any WITH needs an exact-version exception.
export const ALLOWED_WITH = new Set([]);
const EXCEPTION_FIELDS = ['license', 'reason', 'approvedBy', 'approvedOn'];
// LICENSE, LICENSE-MIT, NOTICE.md, ...; CopyrightNotice.txt (tslib); *.LICENSE.txt extracted from bundles (webpack)
const NOTICE_FILE = /^(?:(?:licen[cs]e|copying|notice|copyright)(?:[-._].*)?|copyright\w*\.txt|.+\.licen[cs]e(?:\.txt)?)$/i;

export const isMissingLicense = (expr) =>
  typeof expr !== 'string' || /^\s*(unknown|noassertion|none)?\s*$/i.test(expr);

/** Parses an SPDX expression into { id, with? } | { op: 'and' | 'or', args }. Throws on malformed input. */
export function parseSpdx(expr) {
  const tokens = expr.match(/\(|\)|[^\s()]+/g) ?? [];
  let pos = 0;
  const peekOp = () => tokens[pos]?.toUpperCase();
  const binary = (op, next) => {
    const args = [next()];
    while (peekOp() === op) {
      pos += 1;
      args.push(next());
    }
    return args.length === 1 ? args[0] : { op: op.toLowerCase(), args };
  };
  const atom = () => {
    const token = tokens[pos++];
    if (token === '(') {
      const inner = binary('OR', () => binary('AND', atom));
      if (tokens[pos++] !== ')') throw new Error(`unbalanced parentheses in "${expr}"`);
      return inner;
    }
    if (!token || token === ')' || ['AND', 'OR', 'WITH'].includes(token.toUpperCase())) {
      throw new Error(`unexpected ${token ? `"${token}"` : 'end'} in "${expr}"`);
    }
    if (peekOp() !== 'WITH') return { id: token };
    pos += 1;
    const exception = tokens[pos++];
    if (!exception || ['(', ')', 'AND', 'OR', 'WITH'].includes(exception.toUpperCase())) {
      throw new Error(`WITH needs an exception id in "${expr}"`);
    }
    return { id: token, with: exception };
  };
  const tree = binary('OR', () => binary('AND', atom));
  if (pos !== tokens.length) throw new Error(`unexpected "${tokens[pos]}" in "${expr}"`);
  return tree;
}

// A WITH id is free text to the parser; it can add terms as well as permissions, so it must be a reviewed one.
const evaluate = (node, allowedWith) =>
  node.op === 'or' ? node.args.some((arg) => evaluate(arg, allowedWith))
    : node.op === 'and' ? node.args.every((arg) => evaluate(arg, allowedWith))
      : ALLOWED.has(node.id.toLowerCase()) && (node.with === undefined || allowedWith.has(node.with));

/** @returns {{ ok: boolean, why?: string }} */
export function licenseVerdict(expr, allowedWith = ALLOWED_WITH) {
  if (isMissingLicense(expr)) return { ok: false, why: 'missing or unknown license' };
  try {
    return evaluate(parseSpdx(expr), allowedWith) ? { ok: true } : { ok: false, why: `"${expr}" is not on the allowlist` };
  } catch (error) {
    return { ok: false, why: `unparseable license expression: ${error.message}` };
  }
}

/** Flattens `pnpm licenses list --json` (grouped by license) into one entry per installed name@version. */
export function flattenPnpmLicenses(grouped) {
  const packages = [];
  for (const entries of Object.values(grouped)) {
    for (const entry of entries) {
      entry.versions.forEach((version, index) => {
        packages.push({ name: entry.name, version, license: entry.license, homepage: entry.homepage, path: entry.paths?.[index] });
      });
    }
  }
  return packages;
}

/** The name@version keys of every package the lockfile resolves, for all platforms (optional natives included). */
export function lockfilePackageIds(lockText) {
  // a real YAML parse: pnpm 12 writes several documents, and quoting or indentation must not hide a section
  const ids = new Set();
  for (const doc of loadAll(lockText)) {
    for (const id of Object.keys(doc?.packages ?? {})) ids.add(id);
  }
  return ids;
}

/** Lockfile packages pnpm did not install on this platform (other OS/CPU natives, the pinned pnpm itself). */
export function notInstalled(lockIds, installed) {
  const have = new Set(installed.map((pkg) => `${pkg.name}@${pkg.version}`));
  return [...lockIds].filter((id) => !have.has(id)).sort();
}

/** Package entries for lockfile ids whose license comes from registry metadata (`licenses`: id -> license). */
export function lockfileOnlyPackages(ids, licenses) {
  return ids.map((id) => {
    const at = id.lastIndexOf('@');
    return { name: id.slice(0, at), version: id.slice(at + 1), license: licenses.get(id) };
  });
}

export function exceptionProblems(exceptions) {
  const problems = [];
  for (const [id, entry] of Object.entries(exceptions)) {
    if (!/^(@[^/@]+\/)?[^/@]+@[^@\s]+$/.test(id)) problems.push(`exception key "${id}" is not an exact name@version`);
    for (const field of EXCEPTION_FIELDS) {
      if (typeof entry?.[field] !== 'string' || entry[field].trim() === '') problems.push(`exception ${id} has no "${field}"`);
    }
    if (typeof entry?.approvedOn === 'string' && !/^\d{4}-\d{2}-\d{2}$/.test(entry.approvedOn)) {
      problems.push(`exception ${id} approvedOn "${entry.approvedOn}" is not YYYY-MM-DD`);
    }
  }
  return problems;
}

/**
 * Policy over the resolved tree. `lockIds` is the platform-independent set of resolved packages, so an exception
 * for another platform's native package is not stale on this one.
 */
export function policyProblems(packages, exceptions, lockIds) {
  const problems = exceptionProblems(exceptions);
  for (const pkg of packages) {
    const id = `${pkg.name}@${pkg.version}`;
    const exception = exceptions[id];
    if (exception) {
      if (!isMissingLicense(pkg.license) && pkg.license !== exception.license) {
        problems.push(`${id}: exception records "${exception.license}" but the package declares "${pkg.license}"`);
      }
      continue;
    }
    const verdict = licenseVerdict(pkg.license);
    if (!verdict.ok) problems.push(`${id}: ${verdict.why} (add an approved exact-version exception or drop the package)`);
  }
  for (const id of Object.keys(exceptions)) {
    if (!lockIds.has(id)) problems.push(`stale exception ${id}: not in the resolved tree (remove it from license-exceptions.json)`);
  }
  return problems;
}

const README_SECTION = ' (License section)';
const ROOT_LICENSE_FILE = /^(?:licen[cs]e|copying)(?:[-._][^/]*)?$/i;

/** License, NOTICE and COPYING files anywhere in the package (nested ones credit vendored code), plus the README's License section. */
export function readNoticeTexts(dir) {
  const read = (file) => readFileSync(join(dir, file), 'utf8').replace(/\r\n?/g, '\n').trim();
  const found = [];
  const visit = (rel) => {
    for (const entry of readdirSync(join(dir, rel), { withFileTypes: true })) {
      const path = rel ? `${rel}/${entry.name}` : entry.name;
      // nested node_modules are other packages with their own entries; symlinks could leave the package
      if (entry.isDirectory() && entry.name !== 'node_modules') visit(path);
      else if (entry.isFile() && NOTICE_FILE.test(entry.name)) found.push(path);
    }
  };
  visit('');
  const notices = found.sort().map((file) => ({ file, text: read(file) }));
  const readme = readdirSync(dir, { withFileTypes: true }).filter((e) => e.isFile()).map((e) => e.name).sort()
    .find((file) => /^readme(\.[a-z]+)?$/i.test(file));
  const section = readme && readmeLicenseSection(read(readme));
  return section ? [...notices, { file: `${readme}${README_SECTION}`, text: section }] : notices;
}

const textOf = (node) => node.value ?? node.children?.map(textOf).join('') ?? '';

// A real Markdown parse: headings inside code fences or HTML comments are not heading nodes, so they can neither
// start nor end a section. Every top-level License/Licence/Licensing/Copyright section is kept (decorations such as
// "❯ License" or "📄 License" may come first), each running to the next heading of the same or a higher level.
const NOTICE_HEADING = /^[^\p{L}\p{N}]*(?:licen(?:[cs]e|sing)|copyright)\b/iu;

export function readmeLicenseSection(markdown) {
  const headings = fromMarkdown(markdown).children.filter((node) => node.type === 'heading');
  const sections = [];
  let takenUntil = -1;
  headings.forEach((heading, index) => {
    const start = heading.position.start.offset;
    if (start < takenUntil || !NOTICE_HEADING.test(textOf(heading).trim())) return;
    const next = headings.slice(index + 1).find((h) => h.depth <= heading.depth);
    takenUntil = next?.position.start.offset ?? markdown.length;
    const raw = markdown.slice(start, heading.position.end.offset).trim();
    const body = markdown.slice(heading.position.end.offset, takenUntil).replace(/\n{3,}/g, '\n\n').trim();
    if (body !== '') sections.push(`${raw}\n\n${body}`);
  });
  return sections.length > 0 ? sections.join('\n\n') : null;
}

const byNameThenVersion = (a, b) =>
  a.name < b.name ? -1 : a.name > b.name ? 1 : a.version < b.version ? -1 : a.version > b.version ? 1 : 0;

function httpUrl(value) {
  const raw = typeof value === 'string' ? value : value?.url;
  if (typeof raw !== 'string') return undefined;
  const url = raw.trim().replace(/^git\+/, '').replace(/^git:\/\//, 'https://').replace(/\.git$/, '');
  return /^https?:\/\/[^\s"'<>]+$/.test(url) ? url : undefined;
}

const licenseIds = (node) => (node.op ? node.args.flatMap(licenseIds) : [node.id]);

const authorName = (author) => (typeof author === 'string' ? author.trim() : author?.name?.trim()) || undefined;

/**
 * Notices for the production tree. Each package carries `texts` ([{ file, text }]) read from disk. A package that
 * ships no text but declares an allowlisted license is listed with its metadata and SPDX links instead; any other
 * package without text needs an exception. Output is sorted so repeated runs are byte-identical.
 */
export function buildNotices(packages, exceptions) {
  const problems = [];
  const notices = [...packages].sort(byNameThenVersion).map((pkg) => {
    const id = `${pkg.name}@${pkg.version}`;
    const exception = exceptions[id];
    const license = exception?.license ?? pkg.license;
    const notice = { name: pkg.name, version: pkg.version, license };
    const homepage = httpUrl(pkg.homepage);
    const repository = httpUrl(pkg.repository);
    if (homepage) notice.homepage = homepage;
    if (repository && repository !== homepage) notice.repository = repository;
    if (exception) notice.note = exception.reason;
    notice.texts = [...pkg.texts].sort((a, b) => (a.file < b.file ? -1 : a.file > b.file ? 1 : 0));
    // Attribution from metadata goes on every notice, whatever files a package ships or which policy admitted it.
    const author = authorName(pkg.author);
    if (author) notice.author = author;
    try {
      notice.spdx = [...new Set(licenseIds(parseSpdx(license)))].map((spdxId) => `https://spdx.org/licenses/${spdxId}.html`);
    } catch {
      // an unparseable expression is reported by the policy check; the notice keeps the raw license text
    }
    // its own license: a LICENSE/LICENCE/COPYING file at the package root (NOTICE and nested files only credit others)
    if (!notice.texts.some((text) => ROOT_LICENSE_FILE.test(text.file))) {
      if (!exception && !licenseVerdict(pkg.license).ok) {
        problems.push(`${id}: no license file in the package and ${licenseVerdict(pkg.license).why}`);
      } else {
        notice.missingText = `No license file shipped in the package; licensed ${license} per ${exception ? 'the approved exception' : 'package metadata'}`;
      }
    }
    return notice;
  });
  return { notices, problems };
}

export const serializeNotices = (notices) => `${JSON.stringify({ packages: notices }, null, 2)}\n`;

// npm name (optional scope) @ exact semver. The first character is never "-", so an id cannot pass as an option.
const PACKAGE_ID =
  /^(?:@[a-z0-9][a-z0-9._~-]*\/)?[a-z0-9][a-z0-9._~-]*@(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-[0-9a-z-]+(?:\.[0-9a-z-]+)*)?(?:\+[0-9a-z-]+(?:\.[0-9a-z-]+)*)?$/i;

/** True for an exact `name@version` lockfile id, the only shape that is passed to a registry lookup. */
export const isPackageId = (id) => typeof id === 'string' && PACKAGE_ID.test(id);

/**
 * The pnpm that runs this script, as [file, leading args], never through a shell: its JS entry under this Node, or
 * its standalone executable. Both come from npm_execpath, which `pnpm run` sets.
 */
export function pnpmCommand(execpath = process.env.npm_execpath) {
  if (!execpath || !/pnpm/i.test(basename(execpath))) {
    throw new Error('pnpm not found (npm_execpath is not pnpm): run this through pnpm, e.g. pnpm run licenses:check');
  }
  return /\.[cm]?js$/i.test(execpath) ? [process.execPath, [execpath]] : [execpath, []];
}

function pnpmLicenses(prodOnly) {
  const [file, pre] = pnpmCommand();
  const result = spawnSync(file, [...pre, 'licenses', 'list', '--json', ...(prodOnly ? ['--prod'] : [])], {
    cwd: siteRoot,
    encoding: 'utf8',
    maxBuffer: 256 * 1024 * 1024,
  });
  if (result.status !== 0 || !result.stdout?.trim()) {
    throw new Error(`pnpm licenses list failed: ${result.stderr || result.error || 'no output'}`);
  }
  return flattenPnpmLicenses(JSON.parse(result.stdout));
}

const execFileAsync = promisify(execFile);

/** Registry license of one exact lockfile id. `exec` is execFile-shaped (injectable for tests); no shell is used. */
export async function registryLicense(id, exec = execFileAsync, [file, pre] = pnpmCommand()) {
  if (!isPackageId(id)) throw new Error(`lockfile id "${id}" is not an exact name@version; not looked up`);
  try {
    const { stdout } = await exec(file, [...pre, 'view', id, 'license', '--json'], { cwd: siteRoot });
    const value = stdout.trim() ? JSON.parse(stdout) : undefined;
    // very old manifests carry { type, url } instead of an SPDX string
    return typeof value === 'object' && value !== null ? value.type : value;
  } catch (error) {
    throw new Error(`pnpm view ${id} license failed (registry metadata is needed for packages not installed here): ${error.stderr?.trim() || error.message}`);
  }
}

async function registryLicenses(ids, concurrency = 8) {
  const licenses = new Map();
  const queue = [...ids];
  const worker = async () => {
    for (let id = queue.shift(); id !== undefined; id = queue.shift()) licenses.set(id, await registryLicense(id));
  };
  await Promise.all(Array.from({ length: concurrency }, worker));
  return licenses;
}

function productionNotices(exceptions) {
  const packages = pnpmLicenses(true).map((pkg) => {
    const manifest = JSON.parse(readFileSync(join(pkg.path, 'package.json'), 'utf8'));
    return { ...pkg, repository: manifest.repository, author: manifest.author, texts: readNoticeTexts(pkg.path) };
  });
  return buildNotices(packages, exceptions);
}

function fail(title, problems) {
  process.stderr.write(`${title}\n${problems.map((p) => `  - ${p}`).join('\n')}\n`);
  process.exit(1);
}

async function main(command) {
  const exceptions = JSON.parse(readFileSync(exceptionsPath, 'utf8')).packages ?? {};
  if (command === 'check') {
    const lockIds = lockfilePackageIds(readFileSync(lockfilePath, 'utf8'));
    const installed = pnpmLicenses(false);
    const missing = notInstalled(lockIds, installed);
    const lookups = missing.filter(isPackageId);
    const all = [...installed, ...lockfileOnlyPackages(lookups, await registryLicenses(lookups))];
    const problems = [
      ...missing.filter((id) => !isPackageId(id)).map((id) => `${id}: lockfile id is not an exact name@version; not looked up`),
      ...policyProblems(all, exceptions, lockIds),
      ...productionNotices(exceptions).problems,
    ];
    if (problems.length > 0) fail(`License check failed (${problems.length} problem(s)):`, problems);
    process.stdout.write(
      `License check passed: ${all.length} packages (${missing.length} from registry metadata), ${Object.keys(exceptions).length} exceptions.\n`,
    );
  } else if (command === 'generate') {
    const { notices, problems } = productionNotices(exceptions);
    if (problems.length > 0) fail('Third-party notices generation failed:', problems);
    mkdirSync(dirname(outputPath), { recursive: true });
    writeFileSync(outputPath, serializeNotices(notices));
    process.stdout.write(`Wrote ${notices.length} packages to src/data/third-party-notices.generated.json\n`);
  } else {
    fail('Usage: node scripts/licenses.mjs check|generate', [`unknown command "${command ?? ''}"`]);
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await main(process.argv[2]);
