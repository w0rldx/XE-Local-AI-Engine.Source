import { ActionIcon, CopyButton, Tooltip } from "@mantine/core";
import { IconCheck, IconCopy } from "@tabler/icons-react";
import { memo } from "react";
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

/**
 * Shared syntax-highlighted code block (PrismLight + oneDark + copy button). The single chat code-block
 * renderer — used by both the markdown component map and the tool-call card so highlighting, theming, and
 * the copy affordance stay visually consistent across chat.
 *
 * Memoized on (language, code): a completed message's fenced code blocks keep re-tokenizing through Prism on
 * every streaming token of a later turn without this, since the whole message tree re-renders per token.
 */
export const CodeBlock = memo(function CodeBlock({ language, code }: { language: string; code: string }) {
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
});
