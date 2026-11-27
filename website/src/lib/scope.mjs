// One allowlist shared by the docs plugin, the search theme, the link rewriter and the build verifier.
import { matchesGlob } from 'node:path';

export const DOCS_DIR = '../docs';
export const ROUTE_BASE = '/docs';
export const BASE_URL = '/XE-Local-AI-Engine.Source/';
export const REPO_URL = 'https://github.com/w0rldx/XE-Local-AI-Engine.Source';
export const BRANCH = 'develop';
// include replaces the plugin's default '**/*.{md,mdx}', so every glob must carry the extension filter
// (otherwise png/mp4 files are treated as documents).
export const INCLUDE = [
  'user-guide/**/*.{md,mdx}',
  'agentic-support/**/*.{md,mdx}',
  'troubleshooting.md',
  'wiki/**/*.{md,mdx}',
  'adr/**/*.{md,mdx}',
  'runbooks/**/*.{md,mdx}',
  'security/**/*.{md,mdx}',
];

export const isPage = (docsRelPath) => /\.mdx?$/i.test(docsRelPath);

// A docs-relative posix path is published when it is an included page, or a non-page file sitting where an
// included page could (media next to published pages gets bundled, everything else stays on GitHub).
export function isPublished(docsRelPath) {
  const asPage = isPage(docsRelPath) ? docsRelPath : `${docsRelPath.replace(/\.[^./]*$/, '')}.md`;
  return !asPage.startsWith('../') && INCLUDE.some((glob) => matchesGlob(asPage, glob));
}
