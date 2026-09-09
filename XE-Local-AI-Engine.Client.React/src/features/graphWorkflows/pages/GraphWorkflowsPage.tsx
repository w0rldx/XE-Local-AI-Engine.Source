// The one Graph Workflows surface, in two modes over one selection. Router-free by design (it takes `selection` and
// `onSelectionChange` as props, the way `DevWorkflowDetailPage` does), so it renders directly in a unit test and
// `routes/_layout/graph-workflows.tsx` stays a thin adapter.
//
// `runId` set ⇒ the run view; unset ⇒ the editor. This file owns only the choice between them and the page chrome
// they share; each mode owns its own queries, its own state and what a Save or a decision actually does.

import { Stack } from "@mantine/core";
import { IconSitemap } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { TWO_PANE_BREAKPOINT } from "@/core/layout/constants/LayoutBreakpoints";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import { FullHeightPage } from "@/core/ui/components/FullHeightPage/FullHeightPage";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { GraphWorkflowEditorMode } from "@/features/graphWorkflows/components/GraphWorkflowEditorMode";
import { GraphWorkflowRunMode } from "@/features/graphWorkflows/components/GraphWorkflowRunMode";
import type { GraphWorkflowSelection } from "@/features/graphWorkflows/models/GraphWorkflowModels";

export interface GraphWorkflowsPageProps {
	/** Which definition, run, node and tab the URL is on. Read here, never derived from page state. */
	selection: GraphWorkflowSelection;
	onSelectionChange: (next: GraphWorkflowSelection) => void;
}

export function GraphWorkflowsPage({ selection, onSelectionChange }: GraphWorkflowsPageProps) {
	const { t } = useTranslation();
	// `useWindowDimensions` (unlike `useMediaQuery`) reads `innerWidth` synchronously on the first render, so the
	// two-pane layout never flashes as a drawer before settling — Preview's canvas made the same call.
	const { width } = useWindowDimensions();
	const isNarrow = width < TWO_PANE_BREAKPOINT;

	return (
		<FullHeightPage data-testid="graph-workflows-page">
			<Stack gap="sm" h="100%" style={{ minHeight: 0 }}>
				<PageHeader
					title={t("pages.graphWorkflows.title", "Graph Workflows")}
					icon={<IconSitemap size={24} />}
					subtitle={t("pages.graphWorkflows.subtitle", "Author a workflow graph and watch a run of it node by node.")}
				/>
				{selection.runId === undefined ? (
					<GraphWorkflowEditorMode selection={selection} onSelectionChange={onSelectionChange} isNarrow={isNarrow} />
				) : (
					<GraphWorkflowRunMode selection={selection} onSelectionChange={onSelectionChange} isNarrow={isNarrow} />
				)}
			</Stack>
		</FullHeightPage>
	);
}
