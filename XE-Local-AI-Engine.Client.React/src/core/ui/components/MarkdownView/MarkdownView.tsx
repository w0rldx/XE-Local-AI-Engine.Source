import { Box, Text } from "@mantine/core";
import ReactMarkdown, { type Components } from "react-markdown";

import { markdownComponents, remarkGfm } from "@/core/ui/components/MarkdownView/MarkdownComponents";

interface MarkdownViewProps {
	content: string;
	/**
	 * `render` (the default) draws an image; `link` writes its alt text and URL as plain text and fetches nothing.
	 * Use `link` for content nobody vetted, such as model output built from a fetched page: an image's `src` is
	 * requested the moment it renders, so it can carry data out to whoever owns that URL.
	 */
	images?: "render" | "link";
}

/** The shared map with images replaced by their alt text and URL, as text: no `<img>`, no anchor, no request. */
const linkImageComponents: Components = {
	...markdownComponents,
	img({ src, alt }) {
		const label = typeof alt === "string" && alt.length > 0 ? alt : "image";
		return (
			<Text span={true} size="sm" c="dimmed">
				[{label}] {typeof src === "string" ? src : ""}
			</Text>
		);
	},
};

/**
 * Generic react-markdown renderer with Prism syntax highlighting and remark-gfm.
 *
 * The wrapping Box pins the rendered output to its container width and breaks long
 * unbroken tokens (URLs, paths) so the markdown can never push its container wider —
 * e.g. no horizontal scrollbar on a dialog when this is shown in a preview pane.
 * Block-level overflow culprits (code blocks, tables, images) are additionally
 * constrained in MarkdownComponents so they scroll/shrink within their own box.
 */
export function MarkdownView({ content, images = "render" }: MarkdownViewProps) {
	return (
		<Box style={{ maxWidth: "100%", minWidth: 0, overflowWrap: "anywhere" }}>
			<ReactMarkdown components={images === "link" ? linkImageComponents : markdownComponents} remarkPlugins={[remarkGfm]}>
				{content}
			</ReactMarkdown>
		</Box>
	);
}
