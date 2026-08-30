import { Alert } from "@mantine/core";
import { Background, Controls, type Node, type NodeTypes, ReactFlow, ReactFlowProvider, useReactFlow } from "@xyflow/react";
import { useEffect, useMemo } from "react";
import { useTranslation } from "react-i18next";

import "@xyflow/react/dist/style.css";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { DevWorkflowAnchorCard, DevWorkflowNodeCard } from "@/features/devWorkflows/components/DevWorkflowNodeComponents";
import {
	DEV_WORKFLOW_ANCHOR_NODE_TYPE,
	DEV_WORKFLOW_MAX_RENDERED_NODES,
	type DevWorkflowCanvasGraph,
	toDevWorkflowCanvasGraph,
} from "@/features/devWorkflows/models/DevWorkflowGraphModels";
import { type DevWorkflowRunResponse, devWorkflowNodeTypes } from "@/features/devWorkflows/models/DevWorkflowModels";

// React Flow type → component registry, module scope (stable identity) so React Flow does not warn about a fresh
// object every render. Seven entries, one card: the seam for a type that earns its own body later (P4 §2.3.1).
const canvasNodeTypes: NodeTypes = {
	...Object.fromEntries(devWorkflowNodeTypes.map((nodeType) => [nodeType, DevWorkflowNodeCard])),
	[DEV_WORKFLOW_ANCHOR_NODE_TYPE]: DevWorkflowAnchorCard,
} as NodeTypes;

export interface DevWorkflowGraphViewProps {
	readonly run?: DevWorkflowRunResponse | undefined;
	/**
	 * A prebuilt canvas, which is how the read-only DEFINITION view reaches this component (P4 §4, slice B: one
	 * component, two data sources). It wins over `run`, and the cards it carries have no status because nothing ran.
	 */
	readonly graph?: DevWorkflowCanvasGraph;
	readonly selectedNodeRunId?: string;
	readonly onSelect: (nodeRunId: string) => void;
}

function DevWorkflowGraphViewInner({ run, graph: provided, selectedNodeRunId, onSelect }: DevWorkflowGraphViewProps) {
	const { t } = useTranslation();
	const { fitView } = useReactFlow();
	// The layout is deterministic, so recomputing it on a status tick cannot move a node; what an operator WOULD see is
	// the viewport re-framing under their cursor, and that is keyed on the structural key alone.
	const graph = useMemo(() => provided ?? toDevWorkflowCanvasGraph(run), [provided, run]);
	const nodes = useMemo(
		() => graph.nodes.map((node): Node => ({ ...node, selected: node.id === selectedNodeRunId })),
		[graph.nodes, selectedNodeRunId],
	);

	// The structural key is a re-run TRIGGER, not a value this effect reads: re-framing the viewport is right when the
	// graph gains or loses a node, and wrong on a status tick that arrives every few seconds under the operator's cursor.
	// biome-ignore lint/correctness/useExhaustiveDependencies: graph.structuralKey is the deliberate trigger (see above)
	useEffect(() => {
		// Best-effort: fitView rejects when the flow is unmounted mid-frame, which is not a page error.
		fitView().catch(() => undefined);
	}, [fitView, graph.structuralKey]);

	if (graph.isOverCap) {
		return (
			<Alert color="yellow" variant="light" data-testid="dev-workflow-graph-over-cap">
				{t(
					"pages.devWorkflows.graph.overCap",
					"This run has {{count}} nodes — more than the graph draws. Use the Nodes tab to work through them.",
					{ count: graph.nodeRunCount, max: DEV_WORKFLOW_MAX_RENDERED_NODES },
				)}
			</Alert>
		);
	}

	if (graph.nodes.length === 0) {
		return (
			<EmptyState
				message={t("pages.devWorkflows.nodes.empty", "This run has no node-runs yet.")}
				data-testid="dev-workflow-graph-empty"
			/>
		);
	}

	return (
		<div style={{ height: "100%", minHeight: 320 }} data-testid="dev-workflow-graph">
			<ReactFlow
				nodes={nodes}
				edges={graph.edges}
				nodeTypes={canvasNodeTypes}
				// React Flow's default minZoom of 0.5 clamps the initial fitView: a five-rank chain in a ~280px centre
				// pane needs ~0.2, so the graph opened clipped with Zoom Out already disabled and no way back.
				minZoom={0.1}
				nodesDraggable={false}
				nodesConnectable={false}
				edgesFocusable={false}
				fitView={true}
				proOptions={{ hideAttribution: true }}
				aria-label={t("pages.devWorkflows.graph.label", "Workflow graph")}
				onNodeClick={(_event, node) => {
					// Y6: an anchor is a client visual with no server id, so it is never a drill-down target.
					if (node.type !== DEV_WORKFLOW_ANCHOR_NODE_TYPE) {
						onSelect(node.id);
					}
				}}
			>
				<Background />
				<Controls showInteractive={false} />
			</ReactFlow>
		</div>
	);
}

/**
 * The run graph, strictly read-only: nothing here can move, connect or delete a node. It is the second view over the
 * same selection state the node-run table drives — clicking a card is the same `?node=` change as clicking a row —
 * and the table remains the accessible and small-screen path through a run (P4 §2.2, Y8).
 */
export function DevWorkflowGraphView(props: DevWorkflowGraphViewProps) {
	return (
		<ReactFlowProvider>
			<DevWorkflowGraphViewInner {...props} />
		</ReactFlowProvider>
	);
}
