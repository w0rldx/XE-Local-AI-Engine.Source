// The authoring surface: a palette, an auto-arrange button, a slot the page fills with Save / Validate / Start, and
// the React Flow canvas itself.
//
// Presentational on purpose. Every mutation goes through the `editor` state the page owns, so the config panels and
// this canvas cannot disagree about what the graph is. What lives here is the React Flow plumbing nothing else needs:
// the node-type registry, the palette's drag payload, and turning a click into the page's selection.
//
// Layout is the page's business — this is a plain block that fills its container, and there is no drawer here.

import { Alert, Button, Group, Paper, Stack, Text } from "@mantine/core";
import {
	IconArrowsJoin2,
	IconArrowsSplit2,
	IconFlag,
	IconFlagCheck,
	IconGitBranch,
	IconLayoutDistributeVertical,
	IconPlayerPause,
	IconRobot,
	IconTool,
} from "@tabler/icons-react";
import {
	Background,
	Controls,
	type Edge,
	type Node,
	type NodeTypes,
	ReactFlow,
	ReactFlowProvider,
	useReactFlow,
} from "@xyflow/react";
import { type ReactNode, useCallback, useMemo } from "react";
import { useTranslation } from "react-i18next";

import "@xyflow/react/dist/style.css";

import { GraphWorkflowNodeCard } from "@/features/graphWorkflows/components/GraphWorkflowNodeComponents";
import classes from "@/features/graphWorkflows/components/GraphWorkflowNodes.module.css";
import type { GraphWorkflowEditorState } from "@/features/graphWorkflows/hooks/useGraphWorkflowEditor";
import { graphWorkflowNodeTypeByKind } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { type GraphWorkflowNodeKind, graphWorkflowNodeKinds } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import type { GraphWorkflowGraphIssue } from "@/features/graphWorkflows/models/GraphWorkflowValidation";

/**
 * One card for all eight kinds, registered under the type names the mapper writes. Module scope, because React Flow
 * remounts every card when `nodeTypes` is a new object each render.
 */
const nodeTypes: NodeTypes = Object.fromEntries(
	Object.values(graphWorkflowNodeTypeByKind).map((type) => [type, GraphWorkflowNodeCard]),
);

/** The drag payload's own MIME type, so a drop from anywhere else is ignored rather than parsed. */
const PALETTE_MIME = "application/xe-graph-workflow";

const paletteIcons: Record<GraphWorkflowNodeKind, typeof IconRobot> = {
	Start: IconFlag,
	Agent: IconRobot,
	Tool: IconTool,
	Condition: IconGitBranch,
	Parallel: IconArrowsSplit2,
	Join: IconArrowsJoin2,
	Pause: IconPlayerPause,
	End: IconFlagCheck,
};

/**
 * A palette drag payload is boundary data. Parsed defensively, so a malformed or foreign drop is ignored instead of
 * throwing out of the drop handler.
 */
function parsePalettePayload(raw: string): GraphWorkflowNodeKind | undefined {
	try {
		const parsed = JSON.parse(raw) as { kind?: unknown };
		return graphWorkflowNodeKinds.find((kind) => kind === parsed.kind);
	} catch {
		return undefined;
	}
}

export interface GraphWorkflowEditorCanvasProps {
	readonly editor: GraphWorkflowEditorState;
	readonly selectedNodeKey?: string;
	readonly selectedEdgeId?: string;
	/** Sticky single selection: a card click selects, a pane click clears. */
	readonly onSelectNode: (nodeKey: string | undefined) => void;
	readonly onSelectEdge: (edgeId: string | undefined) => void;
	/** Client AND server issues. A keyed one rings its node or edge; the strip below the canvas renders the text. */
	readonly issues: readonly GraphWorkflowGraphIssue[];
	/** The page injects Save / Validate / Start here. */
	readonly toolbar?: ReactNode;
}

function GraphWorkflowEditorCanvasInner({
	editor,
	selectedNodeKey,
	selectedEdgeId,
	onSelectNode,
	onSelectEdge,
	issues,
	toolbar,
}: GraphWorkflowEditorCanvasProps) {
	const { t } = useTranslation();
	const { screenToFlowPosition } = useReactFlow();

	const issueSubjects = useMemo(
		() => new Set(issues.flatMap((issue) => (issue.subject !== undefined && issue.subject.length > 0 ? [issue.subject] : []))),
		[issues],
	);

	// Selection and the issue ring are RENDER state, not editor state: the page owns the selection (it is a search
	// param) and the issue list is merged from two sources, so neither belongs in the node data the graph is built from.
	const renderNodes = useMemo(
		() =>
			editor.nodes.map((node) => ({
				...node,
				selected: node.id === selectedNodeKey,
				data: { ...node.data, hasIssue: issueSubjects.has(node.id) },
			})),
		[editor.nodes, issueSubjects, selectedNodeKey],
	);

	const renderEdges = useMemo(
		() =>
			editor.edges.map((edge) => ({
				...edge,
				selected: edge.id === selectedEdgeId,
				...(issueSubjects.has(edge.id) ? { className: classes["edge-issue"] } : {}),
			})),
		[editor.edges, issueSubjects, selectedEdgeId],
	);

	const onNodeClick = useCallback(
		(_event: React.MouseEvent, node: Node) => {
			onSelectEdge(undefined);
			onSelectNode(node.id);
		},
		[onSelectEdge, onSelectNode],
	);

	const onEdgeClick = useCallback(
		(_event: React.MouseEvent, edge: Edge) => {
			onSelectNode(undefined);
			onSelectEdge(edge.id);
		},
		[onSelectEdge, onSelectNode],
	);

	const onPaneClick = useCallback(() => {
		onSelectNode(undefined);
		onSelectEdge(undefined);
	}, [onSelectEdge, onSelectNode]);

	const onPaletteDragStart = useCallback((event: React.DragEvent, kind: GraphWorkflowNodeKind) => {
		event.dataTransfer.setData(PALETTE_MIME, JSON.stringify({ kind }));
		event.dataTransfer.effectAllowed = "move";
	}, []);

	const onDragOver = useCallback((event: React.DragEvent) => {
		event.preventDefault();
		event.dataTransfer.dropEffect = "move";
	}, []);

	const onDrop = useCallback(
		(event: React.DragEvent) => {
			event.preventDefault();
			const kind = parsePalettePayload(event.dataTransfer.getData(PALETTE_MIME));
			if (kind === undefined) {
				return;
			}
			editor.addNode(kind, screenToFlowPosition({ x: event.clientX, y: event.clientY }));
		},
		[editor, screenToFlowPosition],
	);

	const refusalMessage =
		editor.lastRefusal === undefined
			? undefined
			: editor.lastRefusal.rule === "tooManyNodes"
				? t("pages.graphWorkflows.editor.refusal.tooManyNodes", "This graph already has as many nodes as a run accepts.")
				: t(
						"pages.graphWorkflows.editor.refusal.parallelEdgesBothUnconditional",
						"These two nodes are already joined by an unconditional edge. Give the new one a condition first.",
					);

	return (
		<Stack gap="sm" style={{ height: "100%" }} data-testid="graph-workflow-editor">
			<Group justify="space-between" align="flex-end" wrap="wrap" gap="sm">
				<Group gap="xs" role="group" aria-label={t("pages.graphWorkflows.palette.title", "Add a node")}>
					{graphWorkflowNodeKinds.map((kind) => {
						const Icon = paletteIcons[kind];
						const type = graphWorkflowNodeTypeByKind[kind];
						return (
							// biome-ignore lint/a11y/noStaticElementInteractions: a deliberate drag-source wrapper; the inner Button owns click and keyboard
							// biome-ignore lint/a11y/noNoninteractiveElementInteractions: same — the drag source is the documented React Flow palette pattern
							<div
								key={kind}
								draggable={true}
								onDragStart={(event) => onPaletteDragStart(event, kind)}
								style={{ display: "inline-flex", cursor: "grab" }}
								data-testid={`graph-workflow-palette-drag-${type}`}
							>
								<Button
									size="xs"
									variant="light"
									leftSection={<Icon size={14} />}
									disabled={!editor.canAddNode}
									onClick={() => editor.addNode(kind)}
									data-testid={`graph-workflow-palette-${type}`}
								>
									{t(`pages.graphWorkflows.nodeKind.${kind}`, kind)}
								</Button>
							</div>
						);
					})}
				</Group>
				<Group gap="xs">
					<Button
						size="xs"
						variant="default"
						leftSection={<IconLayoutDistributeVertical size={14} />}
						onClick={editor.autoArrange}
						data-testid="graph-workflow-auto-arrange"
					>
						{t("pages.graphWorkflows.editor.autoArrange", "Auto-arrange")}
					</Button>
					{toolbar}
				</Group>
			</Group>
			{refusalMessage === undefined ? null : (
				<Alert
					color="orange"
					variant="light"
					withCloseButton={true}
					closeButtonLabel={t("pages.graphWorkflows.editor.refusal.dismiss", "Dismiss")}
					onClose={editor.dismissRefusal}
					data-testid="graph-workflow-editor-refusal"
				>
					{refusalMessage}
				</Alert>
			)}
			<Text size="xs" c="dimmed" data-testid="graph-workflow-editor-hint">
				{t(
					"pages.graphWorkflows.editor.deleteHint",
					"Drag a node onto the canvas or click it in the palette. Select a node or a connection and press Delete to remove it.",
				)}
			</Text>
			<Paper withBorder={true} style={{ flex: 1, minHeight: 360 }} data-testid="graph-workflow-canvas">
				<ReactFlow
					nodes={renderNodes}
					edges={renderEdges}
					nodeTypes={nodeTypes}
					onNodesChange={editor.onNodesChange}
					onEdgesChange={editor.onEdgesChange}
					onConnect={editor.onConnect}
					onNodeClick={onNodeClick}
					onEdgeClick={onEdgeClick}
					onPaneClick={onPaneClick}
					onDrop={onDrop}
					onDragOver={onDragOver}
					fitView={true}
					minZoom={0.1}
					deleteKeyCode={["Delete", "Backspace"]}
					proOptions={{ hideAttribution: true }}
					data-testid="graph-workflow-canvas-dropzone"
				>
					<Background />
					<Controls />
				</ReactFlow>
			</Paper>
		</Stack>
	);
}

export function GraphWorkflowEditorCanvas(props: GraphWorkflowEditorCanvasProps) {
	return (
		<ReactFlowProvider>
			<GraphWorkflowEditorCanvasInner {...props} />
		</ReactFlowProvider>
	);
}
