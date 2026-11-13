import { Box, Code, Table, Text } from "@mantine/core";
import { createContext, memo, use, useMemo } from "react";
import type { Components } from "react-markdown";
import ReactMarkdown from "react-markdown";

import { normalizeMath, useMathPlugins } from "@/core/chat/components/ChatMath";
import { markdownImageUrlTransform } from "@/core/chat/components/MarkdownImagePolicy";
import { SafeMarkdownImage } from "@/core/chat/components/SafeMarkdownImage";
import { StreamCaret } from "@/core/chat/components/StreamCaret";
import { CodeBlock } from "@/core/ui/components/CodeBlock/CodeBlock";
import { markdownComponents, remarkGfm } from "@/core/ui/components/MarkdownView/MarkdownComponents";

// Chat bubbles are width-constrained (see ChatMessage's flex bubbles), unlike the shared MarkdownView's usual
// hosts. A long unbreakable token (URL, hash, path) in a paragraph, list item, or inline code span pushes a
// flex bubble wider than its container instead of wrapping, so these overrides layer word-breaking onto the
// shared renderers rather than changing MarkdownView for every consumer.
//
// This MUST stay a module-level constant, not a factory. react-markdown passes components[tag] as the JSX
// element *type*, so a freshly-allocated renderer on every build makes React treat it as a different component
// and unmount/remount the whole subtree — which during streaming re-ran CodeBlock's Prism tokenization on every
// animation frame and silently defeated the memoization in CodeBlock and ChatMessage.
const chatWordBreakOverrides: Partial<Components> = {
	// Model output is untrusted: remote images load only after explicit consent (see SafeMarkdownImage).
	img: SafeMarkdownImage,
	code({ children, className, ...properties }) {
		const language = className?.match(/language-([a-z0-9]+)/i)?.[1]?.toLowerCase();
		const code = String(children).replace(/\n$/, "");

		if (!language) {
			return (
				<Code {...properties} style={{ overflowWrap: "anywhere", wordBreak: "break-word" }}>
					{children}
				</Code>
			);
		}

		// Block code already scrolls horizontally instead of expanding (CodeBlock's own maxWidth/overflowX).
		return <CodeBlock language={language} code={code} />;
	},
	p({ children }) {
		return (
			<Text size="sm" style={{ whiteSpace: "pre-wrap", margin: 0, overflowWrap: "anywhere" }}>
				{children}
			</Text>
		);
	},
	li({ children }) {
		return (
			<li>
				<Text span={true} size="sm" style={{ overflowWrap: "anywhere" }}>
					{children}
				</Text>
			</li>
		);
	},
	// The opposite for tables: the anywhere-break inherited from the wrapper below lets every column shrink to one
	// character, so cells broke mid-word. With normal wrapping a table keeps whole words and the shared renderer's
	// box scrolls it sideways instead.
	table({ children }) {
		return (
			<Box style={{ overflowX: "auto", maxWidth: "100%", overflowWrap: "normal" }}>
				<Table>{children}</Table>
			</Box>
		);
	},
};

const gfmOnly = [remarkGfm];

// The non-caret map has no content dependency at all, so it is built once for every chat bubble in the app.
const staticChatComponents: Components = { ...markdownComponents, ...chatWordBreakOverrides };

// End offset of the trimmed markdown while the caret shows. The caret `p` reads it from context so the renderer
// itself keeps one module-level identity: a per-content `p` made react-markdown remount every paragraph on each
// streamed frame (caret animation restarted, a selection inside the streaming answer was lost).
const CaretEndContext = createContext(-1);

const caretChatComponents: Components = {
	...staticChatComponents,
	p: function CaretParagraph({ children, node }) {
		const caretEnd = use(CaretEndContext);
		const endOffset = node?.position?.end?.offset ?? -1;
		return (
			<Text size="sm" style={{ whiteSpace: "pre-wrap", margin: 0, overflowWrap: "anywhere" }}>
				{children}
				{endOffset >= caretEnd ? <StreamCaret /> : null}
			</Text>
		);
	},
};

interface ChatMarkdownProps {
	content: string;
	withCaret?: boolean;
}

export const ChatMarkdown = memo(function ChatMarkdown({ content, withCaret = false }: ChatMarkdownProps) {
	// Math renders once its lazy chunk is in; until then (and for messages without math) the text renders as before.
	const mathMarkdown = useMemo(() => normalizeMath(content), [content]);
	const math = useMathPlugins(mathMarkdown.includes("$$"));
	const markdown = math ? mathMarkdown : content;
	return (
		// minWidth: 0 lets this shrink inside its flex-item bubble instead of forcing the bubble wider; overflowWrap
		// is a fallback for elements without their own word-breaking style (headings, blockquotes, strong/em).
		<div style={{ minWidth: 0, overflowWrap: "anywhere" }}>
			<CaretEndContext value={markdown.trimEnd().length}>
				<ReactMarkdown
					components={withCaret ? caretChatComponents : staticChatComponents}
					remarkPlugins={math ? [...gfmOnly, ...math.mathRemarkPlugins] : gfmOnly}
					rehypePlugins={math?.mathRehypePlugins}
					urlTransform={markdownImageUrlTransform}
				>
					{markdown}
				</ReactMarkdown>
			</CaretEndContext>
		</div>
	);
});
