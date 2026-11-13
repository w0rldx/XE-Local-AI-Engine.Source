import { Box, Code, Table, Text } from "@mantine/core";
import type { Components } from "react-markdown";
import remarkGfm from "remark-gfm";

import { CodeBlock } from "@/core/ui/components/CodeBlock/CodeBlock";

// Re-export remarkGfm so consumers share the same instance.
export { remarkGfm };

/**
 * Shared react-markdown component map: code/CodeBlock (Prism), p, li, tables, and images as text.
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
	// Table parts go through Mantine too: its alignment and padding target only its own part classes, so a raw <th>
	// kept the browser's centered header and sat offset from its column. GFM column alignment rides in `style`.
	thead: ({ node: _node, ...properties }) => <Table.Thead {...properties} />,
	tbody: ({ node: _node, ...properties }) => <Table.Tbody {...properties} />,
	tr: ({ node: _node, ...properties }) => <Table.Tr {...properties} />,
	th: ({ node: _node, ...properties }) => <Table.Th {...properties} />,
	td: ({ node: _node, ...properties }) => <Table.Td {...properties} />,
	// An image's alt text and URL as plain text: no `<img>`, no anchor, no request. Rendered markdown is often model
	// output nobody vetted, and an image `src` is fetched the moment it renders, so it could carry data out to whoever
	// owns that URL. Chat overrides this with SafeMarkdownImage's click-to-load consent.
	img({ src, alt }) {
		const label = typeof alt === "string" && alt.length > 0 ? alt : "image";
		return (
			<Text span={true} size="sm" c="dimmed">
				[{label}] {typeof src === "string" ? src : ""}
			</Text>
		);
	},
};
