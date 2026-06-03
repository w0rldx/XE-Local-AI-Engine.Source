import { Text } from "@mantine/core";
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

export function ChatMarkdown({ content, withCaret = false }: ChatMarkdownProps) {
	return (
		<ReactMarkdown components={buildComponents(content, withCaret)} remarkPlugins={[remarkGfm]}>
			{content}
		</ReactMarkdown>
	);
}
