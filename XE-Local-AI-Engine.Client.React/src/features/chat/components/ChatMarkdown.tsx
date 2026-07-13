import { Code, Text } from "@mantine/core";
import { memo, useMemo } from "react";
import type { Components } from "react-markdown";
import ReactMarkdown from "react-markdown";

import { CodeBlock } from "@/core/ui/components/CodeBlock/CodeBlock";
import { markdownComponents, remarkGfm } from "@/core/ui/components/MarkdownView/MarkdownComponents";
import { markdownImageUrlTransform } from "@/features/chat/components/MarkdownImagePolicy";
import { SafeMarkdownImage } from "@/features/chat/components/SafeMarkdownImage";
import { StreamCaret } from "@/features/chat/components/StreamCaret";

// Chat bubbles are width-constrained (see ChatMessage's flex bubbles), unlike the shared MarkdownView's usual
// hosts. A long unbreakable token (URL, hash, path) in a paragraph, list item, or inline code span pushes a
// flex bubble wider than its container instead of wrapping, so these overrides layer word-breaking onto the
// shared renderers rather than changing MarkdownView for every consumer.
function chatWordBreakOverrides(): Partial<Components> {
	return {
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
	};
}

// Build a components map that injects a StreamCaret at the end of the last
// paragraph when `withCaret` is true. All other renderers delegate to the
// shared markdownComponents from MarkdownView, layered with the chat-only
// word-break overrides above.
function buildComponents(content: string, withCaret: boolean): Components {
	const overrides = chatWordBreakOverrides();
	if (!withCaret) {
		return { ...markdownComponents, ...overrides };
	}

	const trimmedEnd = content.trimEnd().length;
	return {
		...markdownComponents,
		...overrides,
		p({ children, node }) {
			const endOffset = node?.position?.end?.offset ?? -1;
			const isLastParagraph = endOffset >= trimmedEnd;
			return (
				<Text size="sm" style={{ whiteSpace: "pre-wrap", margin: 0, overflowWrap: "anywhere" }}>
					{children}
					{isLastParagraph ? <StreamCaret /> : null}
				</Text>
			);
		},
	};
}

interface ChatMarkdownProps {
	content: string;
	withCaret?: boolean;
}

export const ChatMarkdown = memo(function ChatMarkdown({ content, withCaret = false }: ChatMarkdownProps) {
	// Memoize the component map so a re-render that changes neither content nor withCaret reuses it. With
	// withCaret false this is the shared stable markdownComponents; the caret variant depends on content (the
	// caret rides the last paragraph), so it rebuilds only when the streaming text actually grows — not on
	// unrelated parent re-renders that React.memo above already screens out.
	const components = useMemo(() => buildComponents(content, withCaret), [content, withCaret]);
	return (
		// minWidth: 0 lets this shrink inside its flex-item bubble instead of forcing the bubble wider; overflowWrap
		// is a fallback for elements without their own word-breaking style (headings, blockquotes, strong/em).
		<div style={{ minWidth: 0, overflowWrap: "anywhere" }}>
			<ReactMarkdown components={components} remarkPlugins={[remarkGfm]} urlTransform={markdownImageUrlTransform}>
				{content}
			</ReactMarkdown>
		</div>
	);
});
