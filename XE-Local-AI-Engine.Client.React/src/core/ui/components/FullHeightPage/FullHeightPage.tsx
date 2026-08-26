import { Box } from "@mantine/core";
import type { ReactNode } from "react";

interface FullHeightPageProps {
	children: ReactNode;
	"data-tour"?: string;
	"data-testid"?: string;
}

// Frame for pages that own their scrolling (e.g. chat): claims the full height of the Layout scroll
// container and lets an inner region scroll instead of the page. Counterpart of PageShell for the
// full-height archetype so the vertical padding stays consistent with normal pages.
//
// The overflow rule is the guard that makes "owns its own scrolling" true rather than aspirational. Without it a child
// whose intrinsic height exceeds this box (a `mih` floor, a tall toolbar, a list that will not shrink) spills into the
// Layout's `overflow-y-auto` container, and the user gets a SECOND, outer scrollbar next to the inner one —
// live-observed on chat and the work-session detail page.
//
// `overflow-y: auto` rather than a flat `hidden`: containing the overflow HERE is what removes the outer bar (the
// Layout container can no longer be overflowed), and every page that already resolves its own scrolling — chat's
// message ScrollArea, the work-session plan/side panels, the canvas — never reaches this fallback, so they still show
// exactly one bar in the region that should have it. A page that has not adopted that pattern (PreviewPage's workflow
// list is the one left) then scrolls inside its own frame instead of having its tail silently clipped, which is the
// failure mode a flat `hidden` would introduce.
//
// The X axis IS clipped: nothing full-height should pan the whole page sideways — wide content carries its own
// horizontal scroller (Table.ScrollContainer), and letting the frame scroll horizontally would drag the page chrome
// out of view with it.
export function FullHeightPage({ children, "data-tour": dataTour, "data-testid": testId }: FullHeightPageProps) {
	return (
		<Box
			py="lg"
			style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0, overflowY: "auto", overflowX: "hidden" }}
			data-tour={dataTour}
			data-testid={testId}
		>
			{children}
		</Box>
	);
}
