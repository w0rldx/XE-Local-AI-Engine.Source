import { ActionIcon, Code, CopyButton, Text, Tooltip } from "@mantine/core";
import { IconCheck, IconCopy } from "@tabler/icons-react";
import type { Components } from "react-markdown";
import ReactMarkdown from "react-markdown";
import { useTranslation } from "react-i18next";
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

function CodeBlock({ language, code }: { language: string; code: string }) {
	const { t } = useTranslation();

	return (
		<div style={{ position: "relative" }}>
			<CopyButton value={code} timeout={2000}>
				{({ copied, copy }) => (
					<Tooltip label={copied ? t("pages.chat.actions.copyCodeSuccess", "Copied") : t("pages.chat.actions.copyCode", "Copy code")} withArrow={true}>
						<ActionIcon
							aria-label={t("pages.chat.actions.copyCode", "Copy code")}
							color={copied ? "teal" : "gray"}
							variant="subtle"
							size="sm"
							onClick={copy}
							style={{ position: "absolute", top: 6, right: 6, zIndex: 1 }}
						>
							{copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
						</ActionIcon>
					</Tooltip>
				)}
			</CopyButton>
			<SyntaxHighlighter customStyle={{ borderRadius: "0.5rem", margin: 0 }} language={language} PreTag="div" style={oneDark}>
				{code}
			</SyntaxHighlighter>
		</div>
	);
}

const markdownComponents: Components = {
	code({ children, className, ...properties }) {
		const language = className?.match(/language-([a-z0-9]+)/i)?.[1]?.toLowerCase();
		const code = String(children).replace(/\n$/, "");

		if (!language) {
			return <Code {...properties}>{children}</Code>;
		}

		return <CodeBlock language={language} code={code} />;
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
