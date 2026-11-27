import assert from 'node:assert/strict';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import rawImgToImage from './raw-img-to-image.mjs';
import rewriteExternalLinks from './rewrite-external-links.mjs';

const REPO_ROOT = fileURLToPath(new URL('../../../', import.meta.url));
const GH = 'https://github.com/w0rldx/XE-Local-AI-Engine.Source';
const WIKI_PAGE = path.join(REPO_ROOT, 'docs/wiki/05-chat.md');
const GUIDE_PAGE = path.join(REPO_ROOT, 'docs/user-guide/README.md');

// Runs both plugins in their configured order over a hand-built mdast and returns the first node's url.
function run(node, sourceFile = WIKI_PAGE) {
  const tree = { type: 'root', children: [{ type: 'paragraph', children: [node] }] };
  rawImgToImage()(tree);
  rewriteExternalLinks()(tree, { path: sourceFile });
  return tree.children[0].children[0];
}
const link = (url) => run({ type: 'link', url, children: [] }).url;

test('in-set page links stay for Docusaurus to resolve', () => {
  assert.equal(link('06-scheduler.md'), '06-scheduler.md');
  assert.equal(link('../adr/0002-development-cloud-egress-carrier.md#context'), '../adr/0002-development-cloud-egress-carrier.md#context');
  assert.equal(link('../troubleshooting.md'), '../troubleshooting.md');
});

test('links outside docs/ go to the GitHub blob view', () => {
  assert.equal(link('../../CHANGELOG.md'), `${GH}/blob/develop/CHANGELOG.md`);
  assert.equal(link('../../LICENSE'), `${GH}/blob/develop/LICENSE`);
});

test('links into excluded docs folders and loose files go to GitHub', () => {
  assert.equal(link('../roadmaps/open-product-decisions.md'), `${GH}/blob/develop/docs/roadmaps/open-product-decisions.md`);
  assert.equal(link('../agent-knowledge.md'), `${GH}/blob/develop/docs/agent-knowledge.md`);
});

test('directory targets go to the GitHub tree view', () => {
  assert.equal(link('../../XE-Local-AI-Engine.Testing.FakeDocker'), `${GH}/tree/develop/XE-Local-AI-Engine.Testing.FakeDocker`);
  assert.equal(link('reference/'), `${GH}/tree/develop/docs/wiki/reference`);
});

test('fragments are kept on rewrite and same-page fragments are untouched', () => {
  assert.equal(link('../../AGENTS.md#validation'), `${GH}/blob/develop/AGENTS.md#validation`);
  assert.equal(link('#scope-at-a-glance'), '#scope-at-a-glance');
});

test('reference definitions are rewritten like links', () => {
  const definition = run({ type: 'definition', identifier: 'cl', label: 'cl', url: '../../CHANGELOG.md#unreleased' });
  assert.equal(definition.url, `${GH}/blob/develop/CHANGELOG.md#unreleased`);
});

test('external and site-absolute URLs are untouched', () => {
  for (const url of ['https://example.com/a.md', 'mailto:a@example.com', '//cdn.example.com/x.png', '/docs/wiki/Home']) {
    assert.equal(link(url), url);
  }
});

test('Markdown images inside the set stay for bundling; outside the set they go to raw', () => {
  const inside = run({ type: 'image', url: 'media/screenshots/chat@2x.png', alt: '' }, GUIDE_PAGE);
  assert.equal(inside.url, 'media/screenshots/chat@2x.png');
  const outside = run({ type: 'image', url: '../audits/figure.png', alt: '' });
  assert.equal(outside.url, `${GH}/raw/develop/docs/audits/figure.png`);
});

test('media links inside the set stay; outside the set they go to raw', () => {
  assert.equal(link('../user-guide/media/clips/chat-streaming.mp4'), '../user-guide/media/clips/chat-streaming.mp4');
  assert.equal(link('../../assets/demo.mp4'), `${GH}/raw/develop/assets/demo.mp4`);
});

test('raw-HTML <img> inside the set becomes a bundled image node', () => {
  const html = { type: 'html', value: '<img src="media/screenshots/chat@2x.png" alt="Chat" width="900">' };
  const image = run(html, GUIDE_PAGE);
  assert.equal(image.type, 'image');
  assert.equal(image.url, 'media/screenshots/chat@2x.png');
  assert.equal(image.alt, 'Chat');
  assert.deepEqual(image.data, { hProperties: { width: '900' } });
});

test('raw-HTML <img> outside the set becomes an image pointing at raw', () => {
  const image = run({ type: 'html', value: "<img src='../audits/figure.png' alt='Figure'>" });
  assert.equal(image.type, 'image');
  assert.equal(image.url, `${GH}/raw/develop/docs/audits/figure.png`);
});
