import { Box } from "@mantine/core";
import type { ReactNode } from "react";

import classes from "@/core/ui/components/StickyBar/StickyBar.module.css";

interface StickyBarProps {
	children: ReactNode;
	/** The edge of the scroll container the bar pins to: a tab bar to the top, a form's action row to the bottom. */
	edge: "top" | "bottom";
	"data-testid"?: string;
}

// Pins its children to an edge of the page scroller while their parent is on screen, then scrolls away with it. An
// ancestor with `overflow: hidden` becomes the scroller instead and the bar stops sticking, which is why the theme
// clips Card rather than hiding its overflow. Pair a top bar over `Tabs.List` with `scrollStuckTabsToTop`.
export function StickyBar({ children, edge, "data-testid": testId }: StickyBarProps) {
	return (
		<Box
			pos="sticky"
			bottom={edge === "bottom" ? 0 : undefined}
			py={edge === "bottom" ? "xs" : undefined}
			className={edge === "top" ? `${classes["bar"]} ${classes["top"]}` : classes["bar"]}
			// Above Mantine's in-flow layers (at most 5, Table), so a Slider thumb or segment label never paints over it;
			// below the nav (100) and overlays (200 and up).
			style={{ zIndex: 10 }}
			data-testid={testId}
		>
			{children}
		</Box>
	);
}
