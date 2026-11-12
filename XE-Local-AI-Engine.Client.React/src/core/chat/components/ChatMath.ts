import { remark } from "remark";
import remarkGfm from "remark-gfm";
import { useEffect, useState } from "react";

// `\[…\]` or `$$…$$` alone on its line becomes a display block; `\[…\]` inside a line, `\(…\)` and a Pandoc-style
// `$…$` (no space inside either dollar, closing one not before a digit, not escaped) become `$$…$$` text math.
// "$5 and $10" stays text.
const MATH = new RegExp(
	[
		/^(?<indent>[ \t]*)(?:\\\[(?<display>(?:(?!\\\])[\s\S])+?)\\\]|\$\$(?<lineDisplay>[^$\n]+?)\$\$)[ \t]*$/.source,
		/\\\[(?<bracket>[^\n]+?)\\\]/.source,
		/\$\$[\s\S]*?\$\$/.source,
		/\\\((?<inline>(?:(?!\n[ \t]*\n)[\s\S])+?)\\\)/.source,
		/(?<![\\$])\$(?=[^\s$])(?<single>[^$\n]*?[^\s$\\])\$(?![\d$])/.source,
	].join("|"),
	"gm",
);

// `\[` is also Markdown's escape for a literal bracket ("see \[1\]"), so bracket contents count as math only with a
// command, a script or a relation in them.
const MATH_LIKE = /\\[a-zA-Z]|[\^_=+<>]/;

interface MathGroups {
	indent?: string;
	display?: string;
	bracket?: string;
	lineDisplay?: string;
	inline?: string;
	single?: string;
}

// The mdast fields this module reads (`mdast` itself is not a direct dependency).
interface SourceNode {
	type: string;
	position?: { start: { offset?: number | undefined }; end: { offset?: number | undefined } } | undefined;
	children?: SourceNode[];
}

const PROTECTED_NODES = new Set(["code", "inlineCode", "html", "image", "definition"]);

const markdownParser = remark().use(remarkGfm);

/** Source ranges the Markdown parser reads as code, raw HTML or a URL; math is never rewritten inside them. */
function codeRanges(markdown: string): [number, number][] {
	const ranges: [number, number][] = [];
	const walk = (node: SourceNode): void => {
		const start = node.position?.start.offset ?? 0;
		const end = node.position?.end.offset ?? 0;
		if (PROTECTED_NODES.has(node.type)) {
			ranges.push([start, end]);
			return;
		}
		if (node.type === "link") {
			// `[label](url)` keeps math in its label; an autolink is all URL.
			const labelEnd = node.children?.at(-1)?.position?.end.offset;
			ranges.push([markdown[start] === "[" && labelEnd !== undefined ? labelEnd : start, end]);
		}
		node.children?.forEach(walk);
	};
	walk(markdownParser.parse(markdown));
	return ranges;
}

/** Rewrites the delimiters local models use into the `$$` forms remark-math parses with single dollars off. */
export function normalizeMath(markdown: string): string {
	let ranges: [number, number][] | undefined;
	let output = "";
	let copied = 0;
	MATH.lastIndex = 0;
	for (let match = MATH.exec(markdown); match !== null; match = MATH.exec(markdown)) {
		const start = match.index;
		const end = start + match[0].length;
		// Parsed only once a candidate turns up, so a message without math costs no second parse.
		ranges ??= codeRanges(markdown);
		const code = ranges.find(([from, to]) => from < end && start < to);
		if (code !== undefined) {
			// A candidate inside code resumes after it; one that only runs into code retries one character on.
			MATH.lastIndex = start >= code[0] ? code[1] : start + 1;
			continue;
		}
		output += markdown.slice(copied, start) + rewriteMath(match);
		copied = end;
	}
	return output + markdown.slice(copied);
}

function rewriteMath(match: RegExpExecArray): string {
	const { indent = "", display, bracket, lineDisplay, inline, single } = match.groups as MathGroups;
	if (((display ?? bracket) !== undefined && !MATH_LIKE.test(display ?? bracket ?? "")) || inline?.trim() === "") {
		return match[0];
	}
	const block = display ?? lineDisplay;
	if (block !== undefined) {
		// The indent keeps a block that stands on its own line inside a list item in that item.
		return `\n${indent}$$\n${indent}${block.trim()}\n${indent}$$\n`;
	}
	// A newline inside text math would put `$$` at a line start, where remark-math reads it as a display fence.
	const text = bracket?.trim() ?? inline?.replace(/\s*\n\s*/g, " ") ?? single;
	// An already `$$`-delimited span is kept as written.
	return text !== undefined ? `$$${text}$$` : match[0];
}

type MathPlugins = typeof import("@/core/chat/components/ChatMathPlugins");

let loadedPlugins: MathPlugins | undefined;
let pluginsLoad: Promise<MathPlugins> | undefined;

/** The math plugins once their lazy chunk has loaded; `undefined` while not needed or still loading. */
export function useMathPlugins(needed: boolean): MathPlugins | undefined {
	const [plugins, setPlugins] = useState(loadedPlugins);
	useEffect(() => {
		if (!needed || plugins) {
			return;
		}
		let active = true;
		pluginsLoad ??= import("@/core/chat/components/ChatMathPlugins").then((module) => {
			loadedPlugins = module;
			return module;
		});
		pluginsLoad.then(
			(module) => active && setPlugins(module),
			// A failed chunk load leaves the math as plain text; the next message that needs it retries.
			() => {
				pluginsLoad = undefined;
			},
		);
		return () => {
			active = false;
		};
	}, [needed, plugins]);
	return needed ? plugins : undefined;
}
