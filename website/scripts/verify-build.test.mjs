import assert from 'node:assert/strict';
import { test } from 'node:test';
import { attrValues, authoredWidthProblems, localRefs, resolveRef, routeFor, searchProblems } from './verify-build.mjs';

const BASE = '/XE-Local-AI-Engine.Source/';

test('routeFor maps file names to permalinks and README to its folder', () => {
  assert.equal(routeFor('wiki/05-chat.md'), 'docs/wiki/05-chat');
  assert.equal(routeFor('adr/0002-development-cloud-egress-carrier.md'), 'docs/adr/0002-development-cloud-egress-carrier');
  assert.equal(routeFor('user-guide/README.md'), 'docs/user-guide');
  assert.equal(routeFor('troubleshooting.md'), 'docs/troubleshooting');
});

test('attrValues reads quoted and unquoted attributes and decodes entities', () => {
  assert.deepEqual(attrValues('<a href=/x data-href="no" href="/y?a=1&amp;b=2" href=\'/z\'>', 'href'), ['/x', '/y?a=1&b=2', '/z']);
});

test('localRefs keeps media src and asset links, drops external, data and fragment refs', () => {
  const html = [
    `<img src=${BASE}assets/images/a.png alt="a > b">`,
    '<video controls><source src="clip.mp4" type=video/mp4></video>',
    '<img src="https://example.com/x.png"><img src="data:image/png;base64,AA"><img src="#x">',
    `<a href="${BASE}assets/files/c.mp4">clip</a><a href="${BASE}docs/wiki/Home">page</a>`,
    `<link rel=stylesheet href=${BASE}assets/css/s.css>`,
  ].join('');
  assert.deepEqual(localRefs(html), [
    `${BASE}assets/images/a.png`,
    'clip.mp4',
    `${BASE}assets/files/c.mp4`,
    `${BASE}assets/css/s.css`,
  ]);
});

test('resolveRef resolves against the page URL under the base URL', () => {
  assert.equal(resolveRef(`${BASE}assets/images/a%40b.png`, 'docs/wiki/05-chat.html'), 'assets/images/a@b.png');
  assert.equal(resolveRef('media/x.png', 'docs/user-guide.html'), 'docs/media/x.png');
  assert.equal(resolveRef('img/x.png', 'index.html'), 'img/x.png');
  assert.equal(resolveRef('/elsewhere/x.png', 'index.html'), null);
});

test('searchProblems flags excluded pages, unindexed pages and an index without headings', () => {
  const routes = new Set(['docs/wiki/05-chat']);
  const page = { t: 'Chat', u: `${BASE}docs/wiki/05-chat` };
  const heading = { t: 'Chat workflow mode', u: `${BASE}docs/wiki/05-chat`, h: '#chat-workflow-mode' };
  assert.deepEqual(searchProblems([{ documents: [page] }, { documents: [heading] }], routes), []);
  const problems = searchProblems([{ documents: [{ t: 'Gate', u: `${BASE}docs/audits/x#y` }] }], routes);
  assert.equal(problems.length, 3);
  assert.match(problems[0], /docs\/audits\/x#y is not an allowlisted page/);
  assert.match(problems[1], /docs\/wiki\/05-chat is not indexed/);
  assert.match(problems[2], /no headings/);
});

test('authoredWidthProblems matches an out-of-scope image rewritten to its GitHub raw URL', () => {
  const markdown = '<img src="../audits/figure.png" width="460">';
  const page = (width) => `<img src=https://github.com/w0rldx/XE-Local-AI-Engine.Source/raw/develop/docs/audits/figure.png width=${width} />`;
  assert.deepEqual(authoredWidthProblems(markdown, page(460)), []);
  assert.equal(authoredWidthProblems(markdown, page(300)).length, 1);
});

test('authoredWidthProblems ignores images inside code fences and inline code', () => {
  const markdown = [
    '```html',
    '<img src="diagram.png" width="460">',
    '```',
    'Write `<img src="diagram.png" width="10">` like this.',
    '<img src="diagram.png" width="900">',
  ].join('\n');
  const page = `<img src=${BASE}assets/images/diagram-0a76.png width=900 />`;
  assert.deepEqual(authoredWidthProblems(markdown, page), []);
});

test('authoredWidthProblems compares a raw <img width> with the hashed image on the built page', () => {
  const markdown = '>   <img src="media/screenshots/smartscreen-1-more-info.png" alt="x" width="460">\n<img src="media/y.png">';
  const page = (width) =>
    `<img alt=x src=${BASE}assets/images/smartscreen-1-more-info-0a764332dcc3a50f93a20fc72a1522aa.png width=${width} height=427 class=img_j6kc />`;
  assert.deepEqual(authoredWidthProblems(markdown, page(460)), []);
  assert.deepEqual(authoredWidthProblems(markdown, page(802)), [
    '<img src="media/screenshots/smartscreen-1-more-info.png"> is 802 wide on the page, authored 460',
  ]);
  assert.deepEqual(authoredWidthProblems(markdown, '<p>no image</p>'), [
    '<img src="media/screenshots/smartscreen-1-more-info.png" width="460"> has no built <img>',
  ]);
  assert.deepEqual(authoredWidthProblems('<img src="https://example.com/x.png" width="10">', ''), []);
});

test('authoredWidthProblems pairs a repeated image with its built copies one-to-one in document order', () => {
  const markdown = '<img src="media/a.png" width="460">\n\n![a](media/a.png)\n\n<img src="../media/a.png" alt="a" width="900">';
  const img = (width) => `<img src=${BASE}assets/images/a-0a76.png${width ? ` width=${width}` : ''}>`;
  assert.deepEqual(authoredWidthProblems(markdown, img(460) + img() + img(900)), []);
  assert.deepEqual(authoredWidthProblems(markdown, img(900) + img() + img(460)), [
    '<img src="media/a.png"> is 900 wide on the page, authored 460',
    '<img src="../media/a.png"> is 460 wide on the page, authored 900',
  ]);
  assert.deepEqual(authoredWidthProblems(markdown, img(460) + img()), ['<img src="../media/a.png" width="900"> has no built <img>']);
});

test('attrValues decodes &amp; last, so an escaped entity stays literal', () => {
  assert.deepEqual(attrValues('<a href="a&amp;quot;b&quot;c&amp;d">', 'href'), ['a&quot;b"c&d']);
});
