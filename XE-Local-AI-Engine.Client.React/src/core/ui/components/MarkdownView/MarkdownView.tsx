import { Box } from "@mantine/core";
import ReactMarkdown from "react-markdown";

import { markdownComponents, remarkGfm } from "@/core/ui/components/MarkdownView/MarkdownComponents";

interface MarkdownViewProps {
	content: string;
}

/**
 * Generic react-markdown renderer with Prism syntax highlighting and remark-gfm.
 *
 * The wrapping Box pins the rendered output to its container width and breaks long
 * unbroken tokens (URLs, paths) so the markdown can never push its container wider —
 * e.g. no horizontal scrollbar on a dialog when this is shown in a preview pane.
 * Block-level overflow culprits (code blocks, tables, images) are additionally
 * constrained in MarkdownComponents so they scroll/shrink within their own box.
 */
export function MarkdownView({ content }: MarkdownViewProps) {
	return (
		<Box style={{ maxWidth: "100%", minWidth: 0, overflowWrap: "anywhere" }}>
			<ReactMarkdown components={markdownComponents} remarkPlugins={[remarkGfm]}>
				{content}
			</ReactMarkdown>
		</Box>
	);
}
