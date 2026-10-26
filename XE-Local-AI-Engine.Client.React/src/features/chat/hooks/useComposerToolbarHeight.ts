import type { CSSProperties, RefObject } from "react";
import { useEffect, useRef, useState } from "react";

// The composer toolbar lives inside the Textarea's native bottomSection (Mantine 9.3+), which is absolutely
// positioned at a fixed height — it does not grow to fit its content. The toolbar is one row at every width, but
// that row's height still follows its contents (a fallback font, a zoomed page, a control taller than 36px), so the
// height is measured live via ResizeObserver and both the section height and the input's reserved bottom padding
// are driven from that measurement — otherwise a taller toolbar either gets clipped or the typed text renders
// underneath it.
const DEFAULT_TOOLBAR_HEIGHT_PX = 48;

interface ComposerToolbarHeight {
	toolbarRef: RefObject<HTMLDivElement | null>;
	composerStyles: { input: CSSProperties; bottomSection: CSSProperties };
}

export function useComposerToolbarHeight(): ComposerToolbarHeight {
	// Measures the toolbar's actual rendered height so the Textarea's fixed-height bottomSection and its own bottom
	// padding stay in sync with however tall the toolbar really is.
	const toolbarRef = useRef<HTMLDivElement>(null);
	const [toolbarHeight, setToolbarHeight] = useState(DEFAULT_TOOLBAR_HEIGHT_PX);

	useEffect(() => {
		const node = toolbarRef.current;
		if (!node || typeof ResizeObserver === "undefined") {
			return undefined;
		}

		const observer = new ResizeObserver((entries) => {
			const measuredHeight = entries[0]?.contentRect.height;
			if (measuredHeight) {
				setToolbarHeight(measuredHeight);
			}
		});
		observer.observe(node);
		return () => observer.disconnect();
	}, []);

	// bottomSection is border-box: its fixed height must include its own paddingBlock (2 × xs) on top of the
	// measured toolbar height, otherwise the toolbar row clips past the composer's bottom edge.
	const composerStyles = {
		input: { paddingBottom: `calc(${toolbarHeight}px + 3 * var(--mantine-spacing-xs))` },
		bottomSection: {
			height: `calc(${toolbarHeight}px + 2 * var(--mantine-spacing-xs))`,
			alignItems: "flex-start",
			paddingBlock: "var(--mantine-spacing-xs)",
		},
	};

	return { toolbarRef, composerStyles };
}
