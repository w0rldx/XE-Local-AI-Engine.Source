import { Code, Text } from "@mantine/core";
import type { Components } from "react-markdown";
import ReactMarkdown from "react-markdown";
import { PrismLight as SyntaxHighlighter } from "react-syntax-highlighter";
import csharp from "react-syntax-highlighter/dist/esm/languages/prism/csharp";
import javascript from "react-syntax-highlighter/dist/esm/languages/prism/javascript";
import json from "react-syntax-highlighter/dist/esm/languages/prism/json";
import jsx from "react-syntax-highlighter/dist/esm/languages/prism/jsx";
import markdown from "react-syntax-highlighter/dist/esm/languages/prism/markdown";
import sql from "react-syntax-highlighter/dist/esm/languages/prism/sql";
import tsx from "react-syntax-highlighter/dist/esm/languages/prism/tsx";
import typescript from "react-syntax-highlighter/dist/esm/languages/prism/typescript";
import { oneDark } from "react-syntax-highlighter/dist/esm/styles/prism";
import remarkGfm from "remark-gfm";

import { StreamCaret } from "@/features/chat/components/StreamCaret";

SyntaxHighlighter.registerLanguage("csharp", csharp);
SyntaxHighlighter.registerLanguage("cs", csharp);
SyntaxHighlighter.registerLanguage("javascript", javascript);
SyntaxHighlighter.registerLanguage("js", javascript);
SyntaxHighlighter.registerLanguage("json", json);
SyntaxHighlighter.registerLanguage("jsx", jsx);
SyntaxHighlighter.registerLanguage("markdown", markdown);
SyntaxHighlighter.registerLanguage("md", markdown);
SyntaxHighlighter.registerLanguage("sql", sql);
SyntaxHighlighter.registerLanguage("tsx", tsx);
SyntaxHighlighter.registerLanguage("typescript", typescript);
SyntaxHighlighter.registerLanguage("ts", typescript);

const markdownComponents: Components = {
	code({ children, className, ...properties }) {
		const language = className?.match(/language-([a-z0-9]+)/i)?.[1]?.toLowerCase();
		const code = String(children).replace(/\n$/, "");

		if (!language) {
			return <Code {...properties}>{children}</Code>;
		}

		return (
			<SyntaxHighlighter customStyle={{ borderRadius: "0.5rem", margin: 0 }} language={language} PreTag="div" style={oneDark}>
				{code}
			</SyntaxHighlighter>
		);
	},
	p({ children }) {
		return (
			<Text size="sm" style={{ whiteSpace: "pre-wrap", margin: 0 }}>
				{children}
			</Text>
		);
	},
	li({ children }) {
		return (
			<li>
				<Text span={true} size="sm">
					{children}
				</Text>
			</li>
		);
	},
};

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
