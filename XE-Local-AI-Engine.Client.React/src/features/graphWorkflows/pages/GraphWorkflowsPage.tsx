import { Button } from "@mantine/core";
import { IconSitemap } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import type { GraphWorkflowSelection } from "@/features/graphWorkflows/models/GraphWorkflowModels";

export interface GraphWorkflowsPageProps {
	/** Which definition, run, node and tab the URL is on. Read here, never derived from page state. */
	selection: GraphWorkflowSelection;
	onSelectionChange: (next: GraphWorkflowSelection) => void;
}

/**
 * The one Graph Workflows surface. Router-free by design (it takes `selection` and `onSelectionChange` as props, the
 * way `DevWorkflowDetailPage` does), so it renders directly in a unit test and `routes/_layout/graph-workflows.tsx`
 * stays a thin adapter.
 *
 * The shell only: the editor, the run view and the event trail land in later slices, which extend this component
 * rather than replacing it.
 */
export function GraphWorkflowsPage({ selection, onSelectionChange }: GraphWorkflowsPageProps) {
	const { t } = useTranslation();

	return (
		<PageShell data-testid="graph-workflows-page">
			<PageHeader
				title={t("pages.graphWorkflows.title", "Graph Workflows")}
				icon={<IconSitemap size={24} />}
				subtitle={t("pages.graphWorkflows.subtitle", "Author a workflow graph and watch a run of it node by node.")}
			/>
			<EmptyState
				icon={<IconSitemap size={32} opacity={0.5} />}
				message={
					selection.runId
						? t("pages.graphWorkflows.empty.run", "The run view is not built yet.")
						: t("pages.graphWorkflows.empty.editor", "No workflow is open. The graph editor is not built yet.")
				}
				action={
					<Button
						variant="default"
						disabled={selection.definitionId === undefined && selection.runId === undefined && selection.nodeKey === undefined}
						data-testid="graph-workflows-clear-selection"
						onClick={() => {
							onSelectionChange({});
						}}
					>
						{t("pages.graphWorkflows.empty.clear", "Clear the selection")}
					</Button>
				}
				data-testid="graph-workflows-empty"
			/>
		</PageShell>
	);
}
