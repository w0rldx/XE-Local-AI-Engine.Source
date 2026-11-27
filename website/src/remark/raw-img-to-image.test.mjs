import assert from 'node:assert/strict';
import { test } from 'node:test';
import rawImgToImage, { applyAuthoredWidths } from './raw-img-to-image.mjs';

const convert = (children) => {
  const tree = { type: 'root', children };
  rawImgToImage()(tree);
  return tree.children;
};
const html = (value) => ({ type: 'html', value });

test('a <p align> wrapper with images becomes a paragraph of image nodes', () => {
  const [paragraph] = convert([html('<p align="center">\n  <img src="a.png" alt="A &amp; B">\n  <img src=b.png />\n</p>')]);
  assert.equal(paragraph.type, 'paragraph');
  assert.deepEqual(
    paragraph.children.map(({ url, alt }) => ({ url, alt })),
    [{ url: 'a.png', alt: 'A & B' }, { url: 'b.png', alt: '' }],
  );
});

test('images nested in a blockquote are converted', () => {
  const [quote] = convert([{ type: 'blockquote', children: [html("<p align='center'><img src='m/x.png' title='T'></p>")] }]);
  const [image] = quote.children[0].children;
  assert.equal(image.url, 'm/x.png');
  assert.equal(image.title, 'T');
});

test('inline html inside a paragraph is replaced in place', () => {
  const [paragraph] = convert([{ type: 'paragraph', children: [html('<img src="x.png">')] }]);
  assert.equal(paragraph.children[0].type, 'image');
});

test('html mixing images with other markup is left alone', () => {
  const node = html('<div><img src="x.png"></div>');
  assert.deepEqual(convert([node]), [node]);
});

test('external, site-absolute and src-less images are left alone', () => {
  for (const value of ['<img src="https://example.com/x.png">', '<img src="/img/x.png">', '<img alt="none">']) {
    assert.deepEqual(convert([html(value)]), [html(value)]);
  }
});

// What Docusaurus's image transform does to an image node: same object, new JSX <img> content sized from the file.
const transformInPlace = (node, width, height) => {
  for (const key of Object.keys(node)) delete node[key];
  const attributes = [{ type: 'mdxJsxAttribute', name: 'src', value: 'x' }];
  if (width) attributes.push({ type: 'mdxJsxAttribute', name: 'width', value: String(width) }, { type: 'mdxJsxAttribute', name: 'height', value: String(height) });
  Object.assign(node, { type: 'mdxJsxTextElement', name: 'img', attributes, children: [] });
};
const sizeOf = (node) => Object.fromEntries(node.attributes.filter((a) => a.name !== 'src').map((a) => [a.name, a.value]));

test('the authored width survives the image transform, with the height scaled to match', () => {
  const [paragraph] = convert([html('<p align="center"><img src="s.png" width="460"><img src="p.png" width="50%"><img src="n.png"></p>')]);
  const [sized, percent, plain] = paragraph.children;
  transformInPlace(sized, 802, 744);
  transformInPlace(percent, 802, 744);
  transformInPlace(plain, 802, 744);
  applyAuthoredWidths()({ type: 'root', children: [paragraph] });
  assert.deepEqual(sizeOf(sized), { width: '460', height: '427' });
  assert.deepEqual(sizeOf(percent), { width: '50%' });
  assert.deepEqual(sizeOf(plain), { width: '802', height: '744' });
});

test('an authored width is added when the file size could not be read', () => {
  const [paragraph] = convert([html('<img src="s.png" width="460">')]);
  transformInPlace(paragraph.children[0]);
  applyAuthoredWidths()({ type: 'root', children: [paragraph] });
  assert.deepEqual(sizeOf(paragraph.children[0]), { width: '460' });
});

test('the authored alt replaces the twice-escaped alt the image transform writes', () => {
  const [paragraph] = convert([html(`<img src="s.png" alt="Don't run">`)]);
  const [image] = paragraph.children;
  transformInPlace(image);
  image.attributes.push({ type: 'mdxJsxAttribute', name: 'alt', value: 'Don&#39;t run' });
  applyAuthoredWidths()({ type: 'root', children: [paragraph] });
  assert.equal(image.attributes.find((a) => a.name === 'alt').value, "Don't run");
});
