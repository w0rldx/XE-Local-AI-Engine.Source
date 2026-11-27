// Turns raw-HTML <img> (alone or inside a <p align> wrapper) into mdast image nodes so Docusaurus bundles the file.
// Raw HTML is otherwise passed through untouched and its relative src breaks on the site's routes.
const IMG = /<img\b[^>]*>/gi;
const WRAPPER = /<\/?p\b[^>]*>/gi;
const ENTITIES = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", '#39': "'" };
// Docusaurus's image transform rewrites each image node in place into a JSX <img> sized from the file and drops
// everything else; it also HTML-escapes alt a second time in md mode (Don't -> Don&#39;t on the page). The node object
// survives that rewrite, so the authored width and alt are keyed on it and re-applied after.
const authored = new WeakMap();

function attr(tag, name) {
  const match = tag.match(new RegExp(`\\s${name}\\s*=\\s*(?:"([^"]*)"|'([^']*)'|([^\\s"'>]+?)(?=\\s|\\/?>))`, 'i'));
  const value = match && (match[1] ?? match[2] ?? match[3]);
  return value?.replace(/&(amp|lt|gt|quot|apos|#39);/g, (_, entity) => ENTITIES[entity]);
}

function toImage(tag) {
  const url = attr(tag, 'src');
  // external and site-absolute images need no bundling; leaving the tag keeps them as they are
  if (!url || /^(?:[a-z][a-z0-9+.-]*:|\/)/i.test(url)) return null;
  const width = attr(tag, 'width');
  const image = {
    type: 'image',
    url,
    alt: attr(tag, 'alt') ?? '',
    title: attr(tag, 'title') ?? null,
    ...(width && { data: { hProperties: { width } } }),
  };
  authored.set(image, { width, alt: image.alt });
  return image;
}

function convert(parent) {
  parent.children = parent.children.flatMap((node) => {
    if (node.type === 'html') {
      const tags = node.value.match(IMG) ?? [];
      // only wrapper-only html (<p ...>, </p>, whitespace) is safe to replace
      const rest = node.value.replace(IMG, '').replace(WRAPPER, '').trim();
      const images = tags.map(toImage);
      if (tags.length && !rest && images.every(Boolean)) {
        return parent.type === 'paragraph' ? images : [{ type: 'paragraph', children: images }];
      }
    }
    if (node.children) convert(node);
    return [node];
  });
}

export default function rawImgToImage() {
  return (tree) => convert(tree);
}

function applyWidths(node) {
  const { width, alt } = (node.type === 'mdxJsxTextElement' && authored.get(node)) || {};
  const attrs = node.attributes;
  const sized = (name) => attrs?.find((a) => a.type === 'mdxJsxAttribute' && a.name === name);
  const altAttr = alt !== undefined && sized('alt');
  if (altAttr) altAttr.value = alt;
  if (width) {
    const [w, h] = [sized('width'), sized('height')];
    // keep the file's aspect ratio for the reserved box; a non-pixel width leaves the height to CSS
    if (h && w && /^\d+$/.test(width)) h.value = String(Math.round((Number(h.value) * Number(width)) / Number(w.value)));
    else if (h) attrs.splice(attrs.indexOf(h), 1);
    if (w) w.value = width;
    else attrs.push({ type: 'mdxJsxAttribute', name: 'width', value: width });
  }
  node.children?.forEach(applyWidths);
}

/** Runs after Docusaurus's default plugins: puts the authored width and alt back on the <img> the image transform made. */
export function applyAuthoredWidths() {
  return (tree) => applyWidths(tree);
}
