// Points relative links whose target is not on the site at GitHub, so docs/ keeps working on GitHub unchanged.
import { statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { BRANCH, DOCS_DIR, REPO_URL, isPage, isPublished } from '../lib/scope.mjs';

const WEBSITE_DIR = fileURLToPath(new URL('../../', import.meta.url));
const REPO_ROOT = path.resolve(WEBSITE_DIR, '..');
const DOCS_ROOT = path.resolve(WEBSITE_DIR, DOCS_DIR);
const MEDIA = /\.(?:png|jpe?g|gif|svg|webp|avif|ico|mp4|webm|mov|mp3|wav|ogg)$/i;
const NOT_RELATIVE = /^(?:[a-z][a-z0-9+.-]*:|\/|#|$)/i;

const toPosix = (p) => p.split(path.sep).join('/');

// Returns the replacement URL, or undefined when the link stays as it is.
export function rewriteUrl(url, sourceFile, { image = false } = {}) {
  if (NOT_RELATIVE.test(url)) return undefined;
  const hash = url.indexOf('#');
  const target = hash < 0 ? url : url.slice(0, hash);
  const fragment = hash < 0 ? '' : url.slice(hash);
  let decoded;
  try {
    decoded = decodeURIComponent(target);
  } catch {
    return undefined;
  }
  const absolute = path.resolve(path.dirname(sourceFile), decoded);
  const repoPath = toPosix(path.relative(REPO_ROOT, absolute));
  if (repoPath === '..' || repoPath.startsWith('../')) return undefined;

  const directory = statSync(absolute, { throwIfNoEntry: false })?.isDirectory() ?? false;
  const docsPath = toPosix(path.relative(DOCS_ROOT, absolute));
  if (!directory && isPublished(docsPath)) return undefined;

  const kind = directory ? 'tree' : !isPage(repoPath) && (image || MEDIA.test(repoPath)) ? 'raw' : 'blob';
  return `${REPO_URL}/${kind}/${BRANCH}/${encodeURI(repoPath)}${fragment}`;
}

function walk(node, sourceFile) {
  if (node.type === 'link' || node.type === 'definition' || node.type === 'image') {
    const url = rewriteUrl(node.url, sourceFile, { image: node.type === 'image' });
    if (url) node.url = url;
  }
  node.children?.forEach((child) => walk(child, sourceFile));
}

export default function rewriteExternalLinks() {
  return (tree, file) => walk(tree, file.path);
}
