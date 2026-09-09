import { Drawer, Paper, Stack } from "@mantine/core";
import type { ReactNode } from "react";

export interface GraphWorkflowResponsivePaneLayoutProps {
	readonly isNarrow: boolean;
	/** The left rail: the definition list in the editor, the run list in a run. Stacked above the main pane when narrow. */
	readonly list: ReactNode;
	readonly main: ReactNode;
	/** The wide viewport's third column. Absent keeps the grid at two columns — run mode never has one. */
	readonly side?: ReactNode;
	readonly narrowTestId: string;
	readonly gridTestId: string;
	readonly sideTestId?: string;
	/** The narrow viewport's side surface, and in a run the node panel at every width. */
	readonly drawer: {
		readonly opened: boolean;
		readonly onClose: () => void;
		readonly title: ReactNode;
		readonly size: string;
		readonly testId: string;
		readonly children: ReactNode;
	};
}

/**
 * The two modes' shared shape: a left rail beside the main pane, an optional third column, and a Drawer that carries
 * the side surface where the grid has no room for one. One component because the two modes' breakpoint behaviour has
 * to agree — a page that stacked in one mode and scrolled sideways in the other at the same width is one surface
 * behaving as two.
 *
 * This is layout only. Everything about WHAT is in a pane stays in the mode that owns it (D2/D10: nothing is shared
 * with the Development Workflows surface).
 */
export function GraphWorkflowResponsivePaneLayout({
	isNarrow,
	list,
	main,
	side,
	narrowTestId,
	gridTestId,
	sideTestId,
	drawer,
}: GraphWorkflowResponsivePaneLayoutProps) {
	return (
		<>
			{isNarrow ? (
				// Stacked rather than dropped: without the list there is no way to reach another definition or run from a
				// phone, and the toolbar's back button is not that.
				<Stack gap="sm" style={{ flex: 1, minHeight: 0 }} data-testid={narrowTestId}>
					{list}
					<div style={{ flex: 1, minHeight: 0 }}>{main}</div>
				</Stack>
			) : (
				<div
					data-testid={gridTestId}
					style={{
						display: "grid",
						gridTemplateColumns: side ? "320px minmax(320px, 1fr) minmax(360px, 420px)" : "320px minmax(320px, 1fr)",
						gridTemplateRows: "minmax(0, 1fr)",
						gap: "var(--mantine-spacing-md)",
						flex: 1,
						minHeight: 0,
						overflowX: "auto",
					}}
				>
					<div style={{ minHeight: 0, overflowY: "auto" }}>{list}</div>
					{main}
					{side ? (
						<Paper withBorder={true} p="sm" style={{ minHeight: 0, overflowY: "auto" }} data-testid={sideTestId}>
							{side}
						</Paper>
					) : null}
				</div>
			)}

			<Drawer
				opened={drawer.opened}
				onClose={drawer.onClose}
				position="right"
				size={drawer.size}
				title={drawer.title}
				attributes={{ content: { "data-testid": drawer.testId } }}
			>
				{drawer.children}
			</Drawer>
		</>
	);
}
