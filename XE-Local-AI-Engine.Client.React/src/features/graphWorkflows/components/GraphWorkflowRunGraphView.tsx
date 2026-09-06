import { Alert, Badge, Group, Stack, Text } from "@mantine/core";
import {
	IconArrowsJoin2,
	IconArrowsSplit2,
	IconFlag,
	IconGitBranch,
	IconPlayerPlay,
	IconRobot,
	IconTool,
	IconUserCheck,
} from "@tabler/icons-react";
import { Background, Controls, Handle, type NodeProps, type NodeTypes, Position, ReactFlow, ReactFlowProvider, useReactFlow } from "@xyflow/react";
import { useEffect, useMemo } from "react";
import { useTranslation } from "react-i18next";

import "@xyflow/react/dist/style.css";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { GraphWorkflowNodeStatusBadge } from "@/features/graphWorkflows/components/GraphWorkflowStatusBadge";
import classes from "@/features/graphWorkflows/components/GraphWorkflowRunNodes.module.css";
import type {
	GraphWorkflowCanvasNode,
	GraphWorkflowCanvasNodeData,
	GraphWorkflowCanvasRunState,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { graphWorkflowNodeTypeByKind } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { GRAPH_WORKFLOW_MAX_RENDERED_NODES, type GraphWorkflowNodeKind } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import type { GraphWorkflowRunCanvas } from "@/features/graphWorkflows/models/GraphWorkflowRunGraph";

const kindIcons: Record<GraphWorkflowNodeKind, typeof IconRobot> = {
	Start: IconPlayerPlay,
	Agent: IconRobot,
	Tool: IconTool,
	Condition: IconGitBranch,
	Parallel: IconArrowsSplit2,
	Join: IconArrowsJoin2,
	Pause: IconUserCheck,
	End: IconFlag,
};

/**
 * The card's border says what the table's badge says, in the same vocabulary. `Queued` gets no border and no motion —
 * it is waiting for a slot another node holds, and a canvas that animated it would claim work is happening on it.
 */
function statusClass(runState: GraphWorkflowCanvasRunState | undefined): string | undefined {
	switch (runState?.status) {
		case "Running":
			return classes["node-running"];
		case "WaitingForApproval":
			return classes["node-waiting"];
		case "Succeeded":
			return classes["node-succeeded"];
		case "Failed":
			return classes["node-failed"];
		default:
			return undefined;
	}
}

function cx(...values: Array<string | false | undefined>): string {
	return values.filter(Boolean).join(" ");
}

/**
 * ONE read-only card for all eight kinds. Deliberately not Lane D1's editor card: this one has no editing affordance to
 * suppress and no issue ring to draw, and importing the editor's would couple the run view to the editor's props.
 *
 * The canvas is a pointer surface; `GraphWorkflowNodeRunTable` stays the keyboard and screen-reader path through a run,
 * which is why this card is a div and not a button.
 */
function GraphWorkflowRunNodeCard({ id, data, selected }: NodeProps<GraphWorkflowCanvasNode>) {
	const { t } = useTranslation();
	const nodeData = data as GraphWorkflowCanvasNodeData;
	const Icon = kindIcons[nodeData.kind];
	const runState = nodeData.runState;

	return (
		<div
			className={cx(classes["node"], selected && classes["node-selected"], statusClass(runState))}
			data-testid={`graph-workflow-run-node-${id}`}
		>
			<Handle type="target" position={Position.Left} isConnectable={false} />
			<Stack gap={4}>
				<div className={classes["node-title"]}>
					<Icon size={14} />
					<Text size="sm" lineClamp={1}>
						{nodeData.label || t(`pages.graphWorkflows.nodeKind.${nodeData.kind}`, nodeData.kind)}
					</Text>
				</div>
				<Group gap={4} wrap="wrap">
					{runState ? (
						<GraphWorkflowNodeStatusBadge status={runState.status} data-testid={`graph-workflow-run-node-status-${id}`} />
					) : null}
					<Badge size="xs" variant="light" color="gray">
						{t(`pages.graphWorkflows.nodeKind.${nodeData.kind}`, nodeData.kind)}
					</Badge>
				</Group>
				{/* A first attempt is the norm and says nothing; a second is the whole story of the node. */}
				{runState && runState.attempt > 1 ? (
					<Text size="xs" c="dimmed" data-testid={`graph-workflow-run-node-attempt-${id}`}>
						{t("pages.graphWorkflows.run.attempt", "attempt {{attempt}}", { attempt: runState.attempt })}
					</Text>
				) : null}
			</Stack>
			<Handle type="source" position={Position.Right} isConnectable={false} />
		</div>
	);
}

// React Flow type → component registry at module scope (stable identity), one entry per kind so a kind that earns its
// own body later can be swapped in without touching the view.
const runNodeTypes: NodeTypes = Object.fromEntries(
	Object.values(graphWorkflowNodeTypeByKind).map((nodeType) => [nodeType, GraphWorkflowRunNodeCard]),
) as NodeTypes;

export interface GraphWorkflowRunGraphViewProps {
	readonly canvas: GraphWorkflowRunCanvas;
	readonly selectedNodeKey?: string;
	readonly onSelectNode: (nodeKey: string | undefined) => void;
}

function GraphWorkflowRunGraphViewInner({ canvas, selectedNodeKey, onSelectNode }: GraphWorkflowRunGraphViewProps) {
	const { t } = useTranslation();
	const { fitView } = useReactFlow();
	const nodes = useMemo(
		() => canvas.nodes.map((node) => ({ ...node, selected: node.id === selectedNodeKey })),
		[canvas.nodes, selectedNodeKey],
	);

	// The structural key is a re-run TRIGGER, not a value this effect reads: re-framing the viewport is right when the
	// graph gains or loses a node, and wrong on a status tick that arrives every few seconds under the operator's cursor.
	// biome-ignore lint/correctness/useExhaustiveDependencies: canvas.structuralKey is the deliberate trigger (see above)
	useEffect(() => {
		// Best-effort: fitView rejects when the flow is unmounted mid-frame, which is not a page error.
		fitView().catch(() => undefined);
	}, [fitView, canvas.structuralKey]);

	// The definition was saved again after this run started, so its edges are NOT the routing this run took. The nodes
	// are still the run's own rows, which is why they are drawn and the edges are not.
	const mismatch = canvas.graphMismatch ? (
		<Alert color="yellow" variant="light" data-testid="graph-workflow-run-graph-mismatch">
			{t(
				"pages.graphWorkflows.run.graphMismatch",
				"The definition changed after this run started, so the connections it ran on are unknown. Showing its nodes only.",
			)}
		</Alert>
	) : null;

	if (canvas.isOverCap) {
		return (
			<Stack gap="xs">
				{mismatch}
				<Alert color="yellow" variant="light" data-testid="graph-workflow-run-graph-over-cap">
					{t(
						"pages.graphWorkflows.run.overCap",
						"This run has {{count}} nodes — more than the graph draws. Use the node table to work through them.",
						{ count: canvas.nodeCount, max: GRAPH_WORKFLOW_MAX_RENDERED_NODES },
					)}
				</Alert>
			</Stack>
		);
	}

	if (nodes.length === 0) {
		return (
			<Stack gap="xs">
				{mismatch}
				<EmptyState
					message={t("pages.graphWorkflows.run.empty", "This run has no nodes yet.")}
					data-testid="graph-workflow-run-graph-empty"
				/>
			</Stack>
		);
	}

	return (
		<Stack gap="xs" style={{ height: "100%" }}>
			{mismatch}
			<div style={{ height: "100%", minHeight: 320 }} data-testid="graph-workflow-run-graph">
				<ReactFlow
					nodes={nodes}
					edges={canvas.edges}
					nodeTypes={runNodeTypes}
					// React Flow's default minZoom of 0.5 clamps the initial fitView, and a clamped fit opens the graph
					// clipped with Zoom Out already disabled.
					minZoom={0.1}
					nodesDraggable={false}
					nodesConnectable={false}
					edgesFocusable={false}
					elementsSelectable={true}
					fitView={true}
					proOptions={{ hideAttribution: true }}
					aria-label={t("pages.graphWorkflows.run.label", "Run graph")}
					onNodeClick={(_event, node) => onSelectNode(node.id)}
					onPaneClick={() => onSelectNode(undefined)}
				>
					<Background />
					<Controls showInteractive={false} />
				</ReactFlow>
			</div>
		</Stack>
	);
}

/**
 * The run's graph, strictly read-only: nothing here can move, connect or delete a node. It is the second view over the
 * same selection the node-run table drives — clicking a card is the same `?nodeKey=` change as clicking a row — and the
 * table remains the accessible and small-screen path through a run.
 */
export function GraphWorkflowRunGraphView(props: GraphWorkflowRunGraphViewProps) {
	return (
		<ReactFlowProvider>
			<GraphWorkflowRunGraphViewInner {...props} />
		</ReactFlowProvider>
	);
}
