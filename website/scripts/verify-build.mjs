// Checks the built site in build/: only allowlisted docs are published, each of them has a page at its file
// name with an exact edit link, every local media/asset reference resolves inside build/, and the search index
// indexes every allowlisted page, some headings, and nothing from outside the allowlist; a raw <img width> in a doc
// keeps that width on the built page. Exits 1 with one line per problem.
import { existsSync, globSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { BASE_URL, BRANCH, DOCS_DIR, INCLUDE, REPO_URL, ROUTE_BASE } from '../src/lib/scope.mjs';

const WEBSITE_DIR = fileURLToPath(new URL('../', import.meta.url));
const EDIT_PREFIX = `${REPO_URL}/edit/${BRANCH}/docs/`;

const TAG = (names) => new RegExp(`<(?:${names})\\b(?:[^>"']|"[^"]*"|'[^']*')*>`, 'gi');
const ATTR = (name) => new RegExp(`\\s${name}\\s*=\\s*(?:"([^"]*)"|'([^']*)'|([^\\s"'=<>\`]+))`, 'gi');
const decodeEntities = (value) => value.replace(/&amp;/g, '&').replace(/&quot;/g, '"').replace(/&#39;/g, "'");

// Docs route (without baseUrl) for a docs-relative source: file name without extension, README/index = its folder.
export function routeFor(source) {
  const route = source.replace(/\.mdx?$/i, '').replace(/(?:^|\/)(?:README|index)$/i, '');
  return route ? `${ROUTE_BASE.slice(1)}/${route}` : ROUTE_BASE.slice(1);
}

export function attrValues(html, name) {
  return [...html.matchAll(ATTR(name))].map((m) => decodeEntities(m[1] ?? m[2] ?? m[3]));
}

// Local references the page needs from build/: media src, plus any src/href into the bundled assets folder.
export function localRefs(html) {
  const media = [...html.matchAll(TAG('img|video|source'))].flatMap(([tag]) => attrValues(tag, 'src'));
  const assets = [...attrValues(html, 'href'), ...attrValues(html, 'src')].filter((v) => v.startsWith(`${BASE_URL}assets/`));
  return [...new Set([...media, ...assets])].filter((v) => !/^(?:[a-z][a-z0-9+.-]*:|\/\/|#)/i.test(v));
}

// build/-relative file path a reference resolves to from a page, or null when it leaves the site.
export function resolveRef(ref, pageFile) {
  const pageUrl = BASE_URL + pageFile.replace(/(?<=^|\/)index\.html$/, '').replace(/\.html$/, '');
  const { pathname } = new URL(ref, `https://site.invalid${pageUrl}`);
  if (!pathname.startsWith(BASE_URL)) return null;
  return decodeURIComponent(pathname.slice(BASE_URL.length));
}

// The built <img> for each local raw <img width> in a source doc must carry that width. The build names the file
// <name>-<hash><ext>, which is how the two are matched; a repeated image pairs its authored and built occurrences
// one-to-one in document order, counting Markdown ![alt](src) images too (they carry no width).
const MD_IMAGE = /!\[(?:[^\]\\]|\\.)*\]\(\s*(?:<([^>]*)>|([^\s)]+))/g;

// Code fences and inline code render as text, not images, so they must not enter the pairing.
const withoutCode = (markdown) =>
  markdown.replace(/^ {0,3}(`{3,}|~{3,})[\s\S]*?^ {0,3}\1[`~]*[ \t]*$/gm, '').replace(/(`+)[^`][\s\S]*?\1/g, '');

export function authoredWidthProblems(source, html) {
  const markdown = withoutCode(source);
  const built = [...html.matchAll(TAG('img'))].map(([tag]) => ({
    file: decodeURIComponent((attrValues(tag, 'src')[0] ?? '').split(/[?#]/)[0].split('/').pop()),
    width: attrValues(tag, 'width')[0],
  }));
  const authored = [
    ...[...markdown.matchAll(TAG('img'))].map((m) => ({ at: m.index, src: attrValues(m[0], 'src')[0], width: attrValues(m[0], 'width')[0] })),
    ...[...markdown.matchAll(MD_IMAGE)].map((m) => ({ at: m.index, src: m[1] ?? m[2] })),
  ].filter(({ src }) => src && !/^(?:[a-z][a-z0-9+.-]*:|\/)/i.test(src)).sort((a, b) => a.at - b.at);
  const seen = new Map();
  const problems = [];
  for (const { src, width } of authored) {
    const { name, ext } = path.posix.parse(src);
    const nth = seen.get(name + ext) ?? 0;
    seen.set(name + ext, nth + 1);
    if (!width) continue;
    // bundled images carry a content hash; out-of-scope ones keep their name on the GitHub raw URL
    const hashed = new RegExp(`^${RegExp.escape(name)}(?:-[0-9a-f]+)?${RegExp.escape(ext)}$`);
    const img = built.filter((candidate) => hashed.test(candidate.file))[nth];
    if (!img) problems.push(`<img src="${src}" width="${width}"> has no built <img>`);
    else if (img.width !== width) problems.push(`<img src="${src}"> is ${img.width ?? 'unsized'} wide on the page, authored ${width}`);
  }
  return problems;
}

export function searchProblems(chunks, routes) {
  const problems = [];
  const documents = chunks.flatMap((chunk) => chunk.documents ?? []);
  for (const { u } of documents) {
    const route = u.split(/[?#]/)[0];
    if (route.startsWith(`${BASE_URL}docs`) && !routes.has(route.slice(BASE_URL.length))) {
      problems.push(`search index: ${u} is not an allowlisted page`);
    }
  }
  // derived from the allowlist rather than a pinned heading, so editing a doc never breaks the site build
  const indexedPages = new Set(documents.filter((d) => !d.h).map((d) => d.u.split(/[?#]/)[0]));
  for (const route of routes) {
    if (!indexedPages.has(`${BASE_URL}${route}`)) problems.push(`search index: ${route} is not indexed`);
  }
  if (!documents.some((d) => d.h)) problems.push('search index: no headings are indexed');
  return problems;
}

export function verify(buildDir) {
  const problems = [];
  const docsRoot = path.resolve(WEBSITE_DIR, DOCS_DIR);
  const sources = globSync(INCLUDE, { cwd: docsRoot }).map((f) => f.split(path.sep).join('/'));
  const routes = new Map(sources.map((source) => [routeFor(source), source]));
  const read = (file) => readFileSync(path.join(buildDir, file), 'utf8');

  // every allowlisted file has a page at its file name, with its exact edit link
  for (const [route, source] of routes) {
    const page = `${route}.html`;
    if (!existsSync(path.join(buildDir, page))) {
      problems.push(`docs/${source}: no page at build/${page} (permalinks must equal file names)`);
      continue;
    }
    const html = read(page);
    const edits = attrValues(html, 'href').filter((href) => href.startsWith(EDIT_PREFIX));
    if (edits.length !== 1 || edits[0] !== `${EDIT_PREFIX}${source}`) {
      problems.push(`docs/${source}: edit link is ${edits.join(', ') || 'missing'}, expected ${EDIT_PREFIX}${source}`);
    }
    const markdown = readFileSync(path.join(docsRoot, source), 'utf8');
    for (const problem of authoredWidthProblems(markdown, html)) problems.push(`docs/${source}: ${problem}`);
  }
  // ADR permalinks equal their file names (checked with every page above; make sure ADRs were among them)
  if (!sources.some((s) => /^adr\/\d{4}-.+\.md$/.test(s))) problems.push('no ADR page was checked');

  const htmlFiles = globSync('**/*.html', { cwd: buildDir }).map((f) => f.split(path.sep).join('/'));
  for (const file of htmlFiles) {
    // every docs page comes from an allowlisted file
    if (file.startsWith('docs/') && !routes.has(file.replace(/\.html$/, ''))) {
      problems.push(`build/${file}: page does not come from an allowlisted docs file`);
    }
    // every local media and asset reference resolves inside build/
    for (const ref of localRefs(read(file))) {
      const target = resolveRef(ref, file);
      const ok = target !== null && existsSync(path.join(buildDir, target)) && statSync(path.join(buildDir, target)).isFile();
      if (!ok) problems.push(`build/${file}: ${ref} does not resolve to a file in build/`);
    }
  }

  // search index: a known heading, nothing outside the allowlist
  const indexes = globSync('search-index*.json', { cwd: buildDir });
  if (indexes.length === 0) problems.push('build/search-index*.json is missing');
  for (const index of indexes) problems.push(...searchProblems(JSON.parse(read(index)), new Set(routes.keys())));
  return problems;
}

if (import.meta.main) {
  const buildDir = path.resolve(process.argv[2] ?? path.join(WEBSITE_DIR, 'build'));
  if (!existsSync(buildDir)) {
    console.error(`verify-build: ${buildDir} does not exist; run the site build first`);
    process.exit(1);
  }
  const problems = verify(buildDir);
  for (const problem of problems) console.error(`verify-build: ${problem}`);
  if (problems.length) process.exit(1);
  console.log(`verify-build: OK (${buildDir})`);
}
