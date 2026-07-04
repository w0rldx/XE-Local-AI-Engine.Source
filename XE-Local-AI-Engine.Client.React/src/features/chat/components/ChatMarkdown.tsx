import { Text } from "@mantine/core";
import { memo, useMemo } from "react";
import type { Components } from "react-markdown";
import ReactMarkdown from "react-markdown";

import { markdownComponents, remarkGfm } from "@/core/ui/components/MarkdownView/MarkdownComponents";
import { StreamCaret } from "@/features/chat/components/StreamCaret";

// Build a components map that injects a StreamCaret at the end of the last
// paragraph when `withCaret` is true. All other renderers delegate to the
// shared markdownComponents from MarkdownView.
function buildComponents(content: string, withCaret: boolean): Components {
	if (!withCaret) {
		return markdownComponents;
	}

	const trimmedEnd = content.trimEnd().length;
	return {
		...markdownComponents,
		p({ children, node }) {
			const endOffset = node?.position?.end?.offset ?? -1;
			const isLastParagraph = endOffset >= trimmedEnd;
			return (
				<Text size="sm" style={{ whiteSpace: "pre-wrap", margin: 0 }}>
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
		<ReactMarkdown components={components} remarkPlugins={[remarkGfm]}>
			{content}
		</ReactMarkdown>
	);
});
