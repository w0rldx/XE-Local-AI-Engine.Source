import { Box, Code, Table, Text } from "@mantine/core";
import type { Components } from "react-markdown";
import remarkGfm from "remark-gfm";

import { CodeBlock } from "@/core/ui/components/CodeBlock/CodeBlock";

// Re-export remarkGfm so consumers share the same instance.
export { remarkGfm };

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
