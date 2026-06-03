import { ActionIcon, Box, Code, CopyButton, Table, Text, Tooltip } from "@mantine/core";
import { IconCheck, IconCopy } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";
import type { Components } from "react-markdown";
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

// Register all Prism languages once at module load (same set as the original ChatMarkdown).
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

// Re-export remarkGfm so consumers share the same instance.
export { remarkGfm };

// biome-ignore lint/style/useComponentExportOnlyModules: intentional shared module — exports both a component map (data) and a helper component used only within this file
function CodeBlock({ language, code }: { language: string; code: string }) {
	const { t } = useTranslation();

	return (
		<div style={{ position: "relative", maxWidth: "100%", minWidth: 0 }}>
			<CopyButton value={code} timeout={2000}>
				{({ copied, copy }) => (
					<Tooltip
						label={copied ? t("pages.chat.actions.copyCodeSuccess", "Copied") : t("pages.chat.actions.copyCode", "Copy code")}
						withArrow={true}
					>
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
			<SyntaxHighlighter
				customStyle={{ borderRadius: "0.5rem", margin: 0, maxWidth: "100%", overflowX: "auto" }}
				language={language}
				PreTag="div"
				style={oneDark}
			>
				{code}
			</SyntaxHighlighter>
		</div>
	);
}

/**
 * Shared react-markdown component map: code/CodeBlock (Prism), p, li.
 * Re-exported for ChatMarkdown to spread and override the `p` renderer
 * with its StreamCaret injection without duplicating the renderers.
 */
export const markdownComponents: Components = {
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
	// A wide table scrolls within its own box instead of widening the container (and the dialog).
	table({ children }) {
		return (
			<Box style={{ overflowX: "auto", maxWidth: "100%" }}>
				<Table>{children}</Table>
			</Box>
		);
	},
	// Images never exceed the container width.
	img({ src, alt }) {
		return (
			<img
				src={typeof src === "string" ? src : undefined}
				alt={typeof alt === "string" ? alt : ""}
				style={{ maxWidth: "100%", height: "auto" }}
			/>
		);
	},
};
