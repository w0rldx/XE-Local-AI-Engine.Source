import { Button, Group, Paper, Stack, Text, TextInput } from "@mantine/core";
import {
	IconBug,
	IconFlag,
	IconFlagCheck,
	IconPlayerPause,
	IconPlayerPlay,
	IconPlayerStop,
	IconRobot,
} from "@tabler/icons-react";
import {
	addEdge,
	Background,
	type Connection,
	Controls,
	type Edge,
	type Node,
	type NodeTypes,
	ReactFlow,
	useEdgesState,
	useNodesState,
} from "@xyflow/react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import "@xyflow/react/dist/style.css";

import { AgentNodeForm } from "@/features/preview/components/AgentNodeForm";
import { AgentNode, DebugNode, EndNode, PauseNode, StartNode } from "@/features/preview/components/PreviewNodeComponents";
import {
	canvasToGraph,
	type PreviewCanvasNode,
	type PreviewCanvasNodeData,
} from "@/features/preview/models/PreviewCanvasModels";
import type { PreviewNodeKind, PreviewWorkflowGraph } from "@/features/preview/models/PreviewWorkflowModels";
import { validatePreviewGraph } from "@/features/preview/models/PreviewWorkflowValidation";

// React Flow type → component registry. Defined module-scope (stable identity) so React Flow does not warn about
// a new nodeTypes object each render.
const nodeTypes: NodeTypes = {
	start: StartNode,
	agent: AgentNode,
	debug: DebugNode,
	pause: PauseNode,
	end: EndNode,
};

// Palette entries: the block kinds the operator can add to the canvas. Start/End are added like any other block
// (the validator enforces exactly one of each), keeping the palette uniform.
const PALETTE: ReadonlyArray<{ kind: PreviewNodeKind; type: string; icon: typeof IconFlag }> = [
	{ kind: "Start", type: "start", icon: IconFlag },
	{ kind: "Agent", type: "agent", icon: IconRobot },
	{ kind: "Debug", type: "debug", icon: IconBug },
	{ kind: "Pause", type: "pause", icon: IconPlayerPause },
	{ kind: "End", type: "end", icon: IconFlagCheck },
];

let nodeSeq = 0;
function nextNodeId(kind: PreviewNodeKind): string {
	nodeSeq += 1;
	return `${kind.toLowerCase()}-${Date.now().toString(36)}-${nodeSeq}`;
}

export interface WorkflowCanvasRunState {
	// True while one of this tab's runs is active (running) — drives Cancel.
	readonly isRunning: boolean;
	// True while the active run is paused — drives Continue.
	readonly isPaused: boolean;
}

export interface WorkflowCanvasProps {
	readonly initialNodes: PreviewCanvasNode[];
	readonly initialEdges: Edge[];
	readonly initialStartText: string;
	readonly runState: WorkflowCanvasRunState;
	// Disable run controls while an execute/cancel/continue request is in flight.
	readonly isControlBusy: boolean;
	// Emits the current graph (canvas → wire shape) — the page wires this to save and execute.
	readonly onExecute: (graph: PreviewWorkflowGraph) => void;
	readonly onCancel: () => void;
	readonly onContinue: () => void;
	// Emitted whenever the canvas graph changes (node/edge edits, start text) so the page can persist live edits
	// on Save. The page holds the latest graph; the canvas owns the editing state.
	readonly onGraphChange: (graph: PreviewWorkflowGraph) => void;
}

// The React Flow editing canvas: a palette to add Start/Agent/Debug/Pause/End blocks, connect them (linear
// chain), pan/zoom, and a per-node config panel (AgentNodeForm for Agent nodes). The run toolbar disables Execute
// when the graph is invalid (client mirror of the backend validator), offers Cancel while running and Continue
// while paused. The parent owns run lifecycle + persistence; this component owns only the graph editing state and
// emits the current graph on Execute.
export function WorkflowCanvas({
	initialNodes,
	initialEdges,
	initialStartText,
	runState,
	isControlBusy,
	onExecute,
	onCancel,
	onContinue,
	onGraphChange,
}: WorkflowCanvasProps) {
	const { t } = useTranslation();
	const [nodes, setNodes, onNodesChange] = useNodesState<PreviewCanvasNode>(initialNodes);
	const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>(initialEdges);
	const [startText, setStartText] = useState(initialStartText);
	const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null);

	const onConnect = useCallback(
		(connection: Connection) => setEdges((current) => addEdge(connection, current)),
		[setEdges],
	);

	const addNode = useCallback(
		(kind: PreviewNodeKind, type: string) => {
			const id = nextNodeId(kind);
			const data: PreviewCanvasNodeData = kind === "Agent" ? { kind, label: "", instructions: "", model: "" } : { kind };
			const node: PreviewCanvasNode = {
				id,
				type,
				position: { x: 320, y: 80 + nodes.length * 40 },
				data,
			};
			setNodes((current) => [...current, node]);
		},
		[nodes.length, setNodes],
	);

	// Keep the config panel open while the operator edits an Agent node. A marquee/shift gesture transiently
	// reports a multi-node (or zero-node) selection, which must NOT collapse the panel — so only adopt a new
	// selection when exactly one node is picked; otherwise the last single selection stays sticky. An explicit
	// deselect (clicking the empty pane) is handled by onPaneClick below.
	const onSelectionChange = useCallback(({ nodes: selected }: { nodes: Node[] }) => {
		if (selected.length === 1) {
			setSelectedNodeId(selected[0]?.id ?? null);
		}
	}, []);

	// Clicking the empty canvas pane is an explicit deselect: close the config panel.
	const onPaneClick = useCallback(() => setSelectedNodeId(null), []);

	// Patch the selected Agent node's data (label/instructions/model/reasoningEffort) from the config panel.
	const patchSelectedNode = useCallback(
		(patch: Partial<PreviewCanvasNodeData>) => {
			if (selectedNodeId === null) {
				return;
			}
			setNodes((current) =>
				current.map((node) =>
					node.id === selectedNodeId ? { ...node, data: { ...node.data, ...patch } } : node,
				),
			);
		},
		[selectedNodeId, setNodes],
	);

	const selectedNode = useMemo(
		() => nodes.find((node) => node.id === selectedNodeId) ?? null,
		[nodes, selectedNodeId],
	);

	// Current graph (canvas → wire shape) + client-side validity for the Execute gate.
	const graph = useMemo(() => canvasToGraph(nodes, edges, startText), [nodes, edges, startText]);
	const validation = useMemo(() => validatePreviewGraph(graph), [graph]);

	// Lift the live graph up so the page can Save the current edits (the canvas owns editing state; the page owns
	// persistence). Fires on every node/edge/start-text change.
	useEffect(() => {
		onGraphChange(graph);
	}, [graph, onGraphChange]);

	const executeDisabled = !validation.isValid || isControlBusy || runState.isRunning || runState.isPaused;

	return (
		<Stack gap="sm" style={{ height: "100%" }}>
			<Group justify="space-between" align="flex-end" wrap="wrap">
				<Group gap="xs">
					{PALETTE.map((entry) => (
						<Button
							key={entry.kind}
							size="xs"
							variant="light"
							leftSection={<entry.icon size={14} />}
							onClick={() => addNode(entry.kind, entry.type)}
							data-testid={`preview-palette-${entry.type}`}
						>
							{t(`pages.preview.nodes.${entry.type}`, entry.kind)}
						</Button>
					))}
				</Group>

				<Group gap="xs">
					<Button
						leftSection={<IconPlayerPlay size={16} />}
						disabled={executeDisabled}
						onClick={() => onExecute(graph)}
						data-testid="preview-execute"
					>
						{t("pages.preview.toolbar.execute", "Execute")}
					</Button>
					{runState.isPaused ? (
						<Button
							color="orange"
							leftSection={<IconPlayerPlay size={16} />}
							disabled={isControlBusy}
							onClick={onContinue}
							data-testid="preview-continue"
						>
							{t("pages.preview.toolbar.continue", "Continue")}
						</Button>
					) : null}
					{runState.isRunning || runState.isPaused ? (
						<Button
							color="red"
							variant="light"
							leftSection={<IconPlayerStop size={16} />}
							disabled={isControlBusy}
							onClick={onCancel}
							data-testid="preview-cancel"
						>
							{t("pages.preview.toolbar.cancel", "Cancel")}
						</Button>
					) : null}
				</Group>
			</Group>

			<TextInput
				label={t("pages.preview.startText.label", "Start input")}
				placeholder={t("pages.preview.startText.placeholder", "Seed text the Start node emits…")}
				value={startText}
				onChange={(event) => setStartText(event.currentTarget.value)}
				data-testid="preview-start-text"
			/>

			{!validation.isValid ? (
				<Text size="xs" c="red" data-testid="preview-validation">
					{validation.errorKeys.map((key) => t(key)).join(" ")}
				</Text>
			) : null}

			<Group align="stretch" gap="sm" style={{ flex: 1, minHeight: 360 }} wrap="nowrap">
				<Paper withBorder={true} style={{ flex: 1, minWidth: 0 }} data-testid="preview-canvas">
					<ReactFlow
						nodes={nodes}
						edges={edges}
						nodeTypes={nodeTypes}
						onNodesChange={onNodesChange}
						onEdgesChange={onEdgesChange}
						onConnect={onConnect}
						onSelectionChange={onSelectionChange}
						onPaneClick={onPaneClick}
						fitView={true}
						proOptions={{ hideAttribution: true }}
					>
						<Background />
						<Controls />
					</ReactFlow>
				</Paper>

				{selectedNode?.data.kind === "Agent" ? (
					<Paper withBorder={true} p="sm" style={{ width: 360 }} data-testid="preview-node-config">
						<AgentNodeForm data={selectedNode.data} onChange={patchSelectedNode} />
					</Paper>
				) : null}
			</Group>
		</Stack>
	);
}
