import { Button, Drawer, Loader, Stack, Tabs } from "@mantine/core";
import { useCallback, useMemo } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { ResponsivePaneLayout } from "@/core/ui/components/ResponsivePaneLayout/ResponsivePaneLayout";
import { GraphWorkflowEventsTab } from "@/features/graphWorkflows/components/GraphWorkflowEventsTab";
import { GraphWorkflowNodePanel } from "@/features/graphWorkflows/components/GraphWorkflowNodePanel";
import { GraphWorkflowNodeRunTable } from "@/features/graphWorkflows/components/GraphWorkflowNodeRunTable";
import { GraphWorkflowRunGraphView } from "@/features/graphWorkflows/components/GraphWorkflowRunGraphView";
import { GraphWorkflowRunList } from "@/features/graphWorkflows/components/GraphWorkflowRunList";
import { GraphWorkflowRunToolbar } from "@/features/graphWorkflows/components/GraphWorkflowRunToolbar";
import { useGraphWorkflowRunHub } from "@/features/graphWorkflows/hooks/useGraphWorkflowRunHub";
import type { GraphWorkflowSelection } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { cachedGraphToCanvas, toGraphWorkflowRunCanvas } from "@/features/graphWorkflows/models/GraphWorkflowRunGraph";
import {
	useGraphWorkflowDefinition,
	useGraphWorkflowRun,
	useGraphWorkflowRuns,
} from "@/features/graphWorkflows/queries/useGraphWorkflows";

export interface GraphWorkflowRunModeProps {
	readonly selection: GraphWorkflowSelection;
	readonly onSelectionChange: (next: GraphWorkflowSelection) => void;
	readonly isNarrow: boolean;
}

/** Watching: the run list, the pinned graph this run routed on, its node-run table, and one node's panel in a drawer. */
export function GraphWorkflowRunMode({ selection, onSelectionChange, isNarrow }: GraphWorkflowRunModeProps) {
	const { t } = useTranslation();
	const runId = selection.runId ?? "";

	const runQuery = useGraphWorkflowRun(runId);
	// Mounted for its invalidations only: every byte on screen comes from the REST reads above and below.
	useGraphWorkflowRunHub(runId);

	const run = runQuery.data?.run;
	// The run knows which definition it was started from; the selection is only the fallback while the run loads.
	const definitionId = run?.definitionId ?? selection.definitionId;
	const definitionQuery = useGraphWorkflowDefinition(definitionId);
	const runsQuery = useGraphWorkflowRuns(definitionId);
	const definition = definitionQuery.data;

	// The graph this run PINNED at start (F5-1). It is the shape the run routed on, so it wins over the definition,
	// which may have been edited since; a response older than F5-1 carries none and the canvas falls back.
	const runGraph = runQuery.data?.graph;

	const canvas = useMemo(
		() =>
			toGraphWorkflowRunCanvas({
				run,
				nodeRuns: runQuery.data?.nodeRuns ?? [],
				runGraph,
				// Only passed once it has actually loaded: a pending definition query is not an edited definition.
				definitionGraph: definition === undefined ? undefined : { graph: definition.graph, graphHash: definition.graphHash },
			}),
		[definition, run, runGraph, runQuery.data?.nodeRuns],
	);

	// The Pause node's own configuration, read through the same defensive parse the canvas uses — the node run carries
	// the decision, never the prompt or the allowed set. Off the PINNED graph, falling back to the definition ONLY when
	// it is the graph that ran: a definition edited since could offer a decision this run's gate will refuse, and the
	// notice is exactly the signal that it has been.
	const pauseGraph = runGraph ?? (canvas.graphNotice === undefined ? definition?.graph : undefined);
	const pauseConfig = useMemo(() => {
		if (selection.nodeKey === undefined || pauseGraph === undefined) {
			return undefined;
		}
		// Cached on the graph: without it every node click re-parsed and re-laid-out the whole pinned document.
		const node = cachedGraphToCanvas(pauseGraph).nodes.find((candidate) => candidate.id === selection.nodeKey)?.data;
		if (node?.kind === "ChatInput") {
			// A ChatInput offers exactly one decision, `Answer`, and its prompt is the question the answer form shows.
			return { prompt: node.prompt, allowedDecisions: [], requireComment: false };
		}
		return node?.kind === "Pause"
			? { prompt: node.prompt, allowedDecisions: node.allowedDecisions, requireComment: node.requireComment }
			: undefined;
	}, [pauseGraph, selection.nodeKey]);

	const select = useCallback(
		(next: Partial<GraphWorkflowSelection>) => onSelectionChange({ ...selection, ...next }),
		[onSelectionChange, selection],
	);

	const tab = selection.tab === "events" ? "events" : "runs";

	const main = runQuery.isPending ? (
		<Loader size="sm" data-testid="gw-page-run-loading" />
	) : runQuery.isError || run === undefined ? (
		<InlineErrorAlert
			variant="light"
			message={apiErrorMessage(runQuery.error, t("pages.graphWorkflows.page.runLoadFailed", "This run could not be loaded."))}
			data-testid="gw-page-run-error"
		>
			<Button
				size="xs"
				variant="light"
				onClick={() => select({ runId: undefined, nodeKey: undefined, tab: "editor" })}
				data-testid="gw-page-run-back"
			>
				{t("pages.graphWorkflows.page.backToEditor", "Back to the editor")}
			</Button>
		</InlineErrorAlert>
	) : (
		<Stack gap="sm" h="100%" style={{ minHeight: 0 }} data-testid="gw-page-run-pane">
			<GraphWorkflowRunToolbar
				run={run}
				onBackToEditor={() => onSelectionChange({ definitionId: definitionId ?? selection.definitionId, tab: "editor" })}
			/>
			<Tabs
				value={tab}
				onChange={(value) => select({ tab: value === "events" ? "events" : "runs" })}
				style={{ flex: 1, minHeight: 0, display: "flex", flexDirection: "column" }}
				data-testid="gw-page-run-tabs"
			>
				<Tabs.List>
					<Tabs.Tab value="runs" data-testid="gw-page-tab-runs">
						{t("pages.graphWorkflows.tab.runs", "Runs")}
					</Tabs.Tab>
					<Tabs.Tab value="events" data-testid="gw-page-tab-events">
						{t("pages.graphWorkflows.tab.events", "Events")}
					</Tabs.Tab>
				</Tabs.List>
				<Tabs.Panel
					value="runs"
					pt="xs"
					style={{ flex: 1, minHeight: 0, display: "flex", flexDirection: "column", gap: "var(--mantine-spacing-sm)" }}
				>
					<div style={{ flex: 1, minHeight: 240 }}>
						<GraphWorkflowRunGraphView
							canvas={canvas}
							selectedNodeKey={selection.nodeKey}
							onSelectNode={(nodeKey) => select({ nodeKey })}
						/>
					</div>
					{/* The table is the accessible path through the same rows — a click here and a click on a card are one
					    `nodeKey` change. */}
					<GraphWorkflowNodeRunTable
						nodeRuns={runQuery.data?.nodeRuns ?? []}
						selectedNodeKey={selection.nodeKey}
						onSelectNode={(nodeKey) => select({ nodeKey })}
					/>
				</Tabs.Panel>
				<Tabs.Panel value="events" pt="xs" style={{ flex: 1, minHeight: 0, overflowY: "auto" }}>
					<GraphWorkflowEventsTab runId={runId} />
				</Tabs.Panel>
			</Tabs>
		</Stack>
	);

	// The rail scrolls inside its own column rather than stretching the grid row. Harmless in the stacked narrow mode,
	// where the column has no height to overflow.
	const runList = (
		<div style={{ minHeight: 0, overflowY: "auto" }}>
			<GraphWorkflowRunList
				runs={runsQuery.data ?? []}
				isLoading={runsQuery.isPending}
				error={runsQuery.error}
				selectedRunId={selection.runId}
				onSelectRun={(next) => select({ runId: next, nodeKey: undefined })}
			/>
		</div>
	);

	return (
		<>
			{/* Stacked rather than dropped: without the run list there is no way to reach another run from a phone,
			    and the toolbar's back button is not that. */}
			<ResponsivePaneLayout
				isNarrow={isNarrow}
				narrowMode="stack"
				list={runList}
				main={main}
				narrowTestId="gw-page-run-narrow"
				gridTestId="gw-page-run-grid"
			/>

			{/* A node panel is a drill-down at every width, not a third column: the run's own grid has two. */}
			<Drawer
				opened={selection.nodeKey !== undefined}
				onClose={() => select({ nodeKey: undefined })}
				position="right"
				size={isNarrow ? "95%" : "45%"}
				title={selection.nodeKey ?? t("pages.graphWorkflows.page.nodeTitle", "Node")}
				attributes={{ content: { "data-testid": "gw-page-node-drawer" } }}
			>
				{selection.nodeKey === undefined ? null : (
					<GraphWorkflowNodePanel
						runId={runId}
						nodeKey={selection.nodeKey}
						runStatus={run?.status}
						pauseConfig={pauseConfig}
						onClose={() => select({ nodeKey: undefined })}
					/>
				)}
			</Drawer>
		</>
	);
}
