import { remark } from "remark";
import remarkGfm from "remark-gfm";
import stripMarkdown, { type Options } from "strip-markdown";

// Converts a markdown string (or fragment thereof) to plain, speakable prose for TTS. Built on remark + remark-gfm
// (GitHub-flavored tables/strikethrough/autolinks, matching the app's rendering flavor) + strip-markdown (mdast ->
// plain-text transform). Formatting markers (bold/italic/headings/list bullets/blockquote `>`) are dropped while the
// inner text they wrap is kept; fenced/inline code, images, and raw HTML are dropped entirely so a spoken answer
// never reads a code block or an image URL aloud.

// Minimal structural shape strip-markdown's "remove" handlers receive for the container node types we override below
// (table/tableRow/tableCell all carry a `children` array; the concrete mdast node types aren't imported here since
// `mdast` itself isn't a direct dependency — `strip-markdown`'s own `Options`/`Handler` types are the only contract
// this module needs).
interface ParentNode {
	readonly children: readonly unknown[];
}

const SPACE = { type: "text", value: " " };
const NEWLINE = { type: "text", value: "\n" };

// strip-markdown's defaults keep an image's alt/title text; TTS should never read even that (usually a filename or a
// terse label, not speakable prose), so images are dropped entirely regardless of alt text.
function dropImage(): undefined {
	return undefined;
}

// strip-markdown's defaults drop `table`/`tableCell` outright and leave `tableRow` untouched, which would otherwise
// survive into the stringified output as a real GFM table node (re-emitting `|`/`-` syntax). Hoisting each level's
// children up instead — with a separating space between cells and a newline between rows — keeps just the cell text.
function hoistTableChildren(node: ParentNode): readonly unknown[] {
	return node.children;
}

function hoistRowCells(node: ParentNode): readonly unknown[] {
	return [...node.children, NEWLINE];
}

function hoistCellText(node: ParentNode): readonly unknown[] {
	return [...node.children, SPACE];
}

const stripOptions = {
	remove: [
		["image", dropImage],
		["imageReference", dropImage],
		["table", hoistTableChildren],
		["tableRow", hoistRowCells],
		["tableCell", hoistCellText],
	],
} as unknown as Options;

const processor = remark().use(remarkGfm).use(stripMarkdown, stripOptions);

// remark-stringify backslash-escapes markdown-significant punctuation (e.g. the `:` in "https\://…") so its output
// would still parse back to the same tree — a guarantee this module doesn't need, since the result is spoken, never
// re-parsed. Strip those escapes so an autolink like "https://example.com" is read as-is, not "https colon slash…".
const UNESCAPE_MARKDOWN = /\\([\\`*_{}[\]()#+\-.!:|>~])/g;
const COLLAPSE_WHITESPACE = /\s+/g;

/**
 * Converts a markdown string to plain speakable text: strips formatting markers, code, images, and raw HTML while
 * keeping the prose they wrap (bold/italic/link text, table cell text, list-item/heading/blockquote text). Autolinks
 * and bare URLs are left as-is (their link text IS the URL). Collapses all resulting whitespace/blank lines to single
 * spaces and trims. Returns an empty string when the input has nothing left to speak (e.g. pure code).
 */
export function toSpeakableText(markdown: string): string {
	const stripped = String(processor.processSync(markdown));
	return stripped.replace(UNESCAPE_MARKDOWN, "$1").replace(COLLAPSE_WHITESPACE, " ").trim();
}
