import { Button, Drawer, Group, Paper, Stack, Text, TextInput } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import {
	IconAdjustments,
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
	applyEdgeChanges,
	applyNodeChanges,
	Background,
	type Connection,
	Controls,
	type Edge,
	type EdgeChange,
	type Node,
	type NodeChange,
	type NodeTypes,
	ReactFlow,
	ReactFlowProvider,
	useEdgesState,
	useNodesState,
	useReactFlow,
} from "@xyflow/react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import "@xyflow/react/dist/style.css";

import { TWO_PANE_BREAKPOINT } from "@/core/layout/constants/LayoutBreakpoints";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import { AgentNodeForm } from "@/features/preview/components/AgentNodeForm";
import { AgentNode, DebugNode, EndNode, PauseNode, StartNode } from "@/features/preview/components/PreviewNodeComponents";
import { canvasToGraph, type PreviewCanvasNode, type PreviewCanvasNodeData } from "@/features/preview/models/PreviewCanvasModels";
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

// Parse a palette drag payload defensively: returns the matching palette entry's {kind, type} only when the
// raw string is valid JSON whose type maps to a known PALETTE entry; otherwise null (malformed JSON or a
// foreign drop). Keeps the drop handler from throwing on untrusted dataTransfer contents.
function parsePalettePayload(raw: string): { kind: PreviewNodeKind; type: string } | null {
	try {
		const parsed = JSON.parse(raw) as { type?: unknown };
		const entry = PALETTE.find((candidate) => candidate.type === parsed.type);
		return entry ? { kind: entry.kind, type: entry.type } : null;
	} catch {
		return null;
	}
}

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
function WorkflowCanvasInner({
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
	const { screenToFlowPosition } = useReactFlow();
	const [nodes, setNodes] = useNodesState<PreviewCanvasNode>(initialNodes);
	const [edges, setEdges] = useEdgesState<Edge>(initialEdges);
	const [startText, setStartText] = useState(initialStartText);
	const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null);
	// The shared two-pane threshold, for the reason it exists: a 360px config panel beside a canvas that needs real
	// room to drag blocks around has nothing left once the shell's sidebar has taken its 220px. useWindowDimensions
	// (unlike useMediaQuery) reads innerWidth synchronously on first render, so the two-pane layout never flashes
	// before collapsing. jsdom defaults innerWidth to 1024, so `<` (not `<=`) keeps two-pane the test default.
	const { width } = useWindowDimensions();
	const isNarrow = width < TWO_PANE_BREAKPOINT;
	// Destructured, NOT kept as the handlers object: useDisclosure returns a fresh object literal on every render (and
	// its `toggle` is rebuilt whenever `opened` changes), so depending on the object made the effect below run every
	// render — which re-opened the drawer on the very re-render a manual close caused, making it impossible to dismiss.
	// `open` and `close` are the useCallback-stable members, so the effect re-runs only when its real inputs change.
	const [configDrawerOpened, { open: openConfigDrawer, close: closeConfigDrawer }] = useDisclosure(false);

	// Refs mirror the latest committed editing inputs so any single mutation (nodes OR edges OR start text) can build
	// the lifted graph from the current value of the others without a stale render closure.
	const nodesRef = useRef(nodes);
	nodesRef.current = nodes;
	const edgesRef = useRef(edges);
	edgesRef.current = edges;
	const startTextRef = useRef(startText);
	startTextRef.current = startText;

	// Lift the live graph up so the page can Save the current edits (the canvas owns editing state; the page owns
	// persistence). Emitted from each mutation entry point — rather than a useEffect that watches the derived graph —
	// so the parent does not re-render an extra time on every node/edge/start-text change.
	const emitGraph = useCallback(
		(nextNodes: PreviewCanvasNode[], nextEdges: Edge[], nextStartText: string) => {
			onGraphChange(canvasToGraph(nextNodes, nextEdges, nextStartText));
		},
		[onGraphChange],
	);

	// React Flow's built-in onNodesChange/onEdgesChange apply changes to state internally; wrap them so the resulting
	// graph is also lifted up. applyNode/EdgeChanges reproduce the exact mutation useNodesState/useEdgesState perform.
	const onNodesChange = useCallback(
		(changes: NodeChange<PreviewCanvasNode>[]) => {
			const next = applyNodeChanges(changes, nodesRef.current);
			nodesRef.current = next;
			setNodes(next);
			emitGraph(next, edgesRef.current, startTextRef.current);
		},
		[emitGraph, setNodes],
	);

	const onEdgesChange = useCallback(
		(changes: EdgeChange<Edge>[]) => {
			const next = applyEdgeChanges(changes, edgesRef.current);
			edgesRef.current = next;
			setEdges(next);
			emitGraph(nodesRef.current, next, startTextRef.current);
		},
		[emitGraph, setEdges],
	);

	const onConnect = useCallback(
		(connection: Connection) => {
			const next = addEdge(connection, edgesRef.current);
			edgesRef.current = next;
			setEdges(next);
			emitGraph(nodesRef.current, next, startTextRef.current);
		},
		[emitGraph, setEdges],
	);

	const addNodeAt = useCallback(
		(kind: PreviewNodeKind, type: string, position: { x: number; y: number }) => {
			const id = nextNodeId(kind);
			const data: PreviewCanvasNodeData = kind === "Agent" ? { kind, label: "", instructions: "", model: "" } : { kind };
			const node: PreviewCanvasNode = { id, type, position, data };
			const next = [...nodesRef.current, node];
			nodesRef.current = next;
			setNodes(next);
			emitGraph(next, edgesRef.current, startTextRef.current);
		},
		[emitGraph, setNodes],
	);

	const addNode = useCallback(
		(kind: PreviewNodeKind, type: string) => addNodeAt(kind, type, { x: 320, y: 80 + nodesRef.current.length * 40 }),
		[addNodeAt],
	);

	const onPaletteDragStart = useCallback((event: React.DragEvent, kind: PreviewNodeKind, type: string) => {
		event.dataTransfer.setData("application/xeflow", JSON.stringify({ kind, type }));
		event.dataTransfer.effectAllowed = "move";
	}, []);

	const onDragOver = useCallback((event: React.DragEvent) => {
		event.preventDefault();
		event.dataTransfer.dropEffect = "move";
	}, []);

	const onDrop = useCallback(
		(event: React.DragEvent) => {
			event.preventDefault();
			const raw = event.dataTransfer.getData("application/xeflow");
			if (!raw) {
				return;
			}
			// Drag payload is boundary data — parse defensively so a malformed/foreign drop is ignored rather
			// than throwing out of the drop handler.
			const payload = parsePalettePayload(raw);
			if (payload === null) {
				return;
			}
			const position = screenToFlowPosition({ x: event.clientX, y: event.clientY });
			addNodeAt(payload.kind, payload.type, position);
		},
		[screenToFlowPosition, addNodeAt],
	);

	const handleStartTextChange = useCallback(
		(value: string) => {
			startTextRef.current = value;
			setStartText(value);
			emitGraph(nodesRef.current, edgesRef.current, value);
		},
		[emitGraph],
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
			const next = nodesRef.current.map((node) =>
				node.id === selectedNodeId ? { ...node, data: { ...node.data, ...patch } } : node,
			);
			nodesRef.current = next;
			setNodes(next);
			emitGraph(next, edgesRef.current, startTextRef.current);
		},
		[emitGraph, selectedNodeId, setNodes],
	);

	const selectedNode = useMemo(() => nodes.find((node) => node.id === selectedNodeId) ?? null, [nodes, selectedNodeId]);
	// Only Agent blocks carry editable config; everything else selects without opening a panel.
	const agentNodeData = selectedNode !== null && selectedNode.data.kind === "Agent" ? selectedNode.data : null;
	const hasNodeConfig = agentNodeData !== null;

	// Below the two-pane width the config is a Drawer, and selecting an Agent block IS the request to configure it —
	// so the drawer follows the selection rather than making the operator hunt for a second control. Deselecting (or
	// widening the window back to the two-pane layout) closes it again; the toolbar button below re-opens it after a
	// manual dismiss without disturbing the selection.
	// selectedNodeId is in the dependency list as a re-run TRIGGER, not as a value the effect reads: moving from one
	// Agent block to another leaves isNarrow and hasNodeConfig unchanged, so without it a drawer the operator dismissed
	// would stay shut when they pick a different block to configure.
	// biome-ignore lint/correctness/useExhaustiveDependencies: selectedNodeId is a deliberate re-run trigger (see above)
	useEffect(() => {
		if (isNarrow && hasNodeConfig) {
			openConfigDrawer();
			return;
		}
		closeConfigDrawer();
	}, [isNarrow, hasNodeConfig, selectedNodeId, openConfigDrawer, closeConfigDrawer]);

	const nodeConfig = agentNodeData === null ? null : <AgentNodeForm data={agentNodeData} onChange={patchSelectedNode} />;

	// Current graph (canvas → wire shape) + client-side validity for the Execute gate. The same derived graph is
	// passed to onExecute below; the live lift-up to the parent happens in emitGraph from each mutation entry point.
	const graph = useMemo(() => canvasToGraph(nodes, edges, startText), [nodes, edges, startText]);
	const validation = useMemo(() => validatePreviewGraph(graph), [graph]);

	const executeDisabled = !validation.isValid || isControlBusy || runState.isRunning || runState.isPaused;

	return (
		<Stack gap="sm" style={{ height: "100%" }}>
			<Group justify="space-between" align="flex-end" wrap="wrap">
				<Group gap="xs">
					{PALETTE.map((entry) => (
						// biome-ignore lint/a11y/noStaticElementInteractions: intentional DnD drag-source wrapper; inner Button owns keyboard/click
						// biome-ignore lint/a11y/noNoninteractiveElementInteractions: same — drag source div is a deliberate DnD pattern
						<div
							key={entry.kind}
							draggable={true}
							onDragStart={(e) => onPaletteDragStart(e, entry.kind, entry.type)}
							style={{ display: "inline-flex", cursor: "grab" }}
							data-testid={`preview-palette-drag-${entry.type}`}
						>
							<Button
								size="xs"
								variant="light"
								leftSection={<entry.icon size={14} />}
								onClick={() => addNode(entry.kind, entry.type)}
								data-testid={`preview-palette-${entry.type}`}
							>
								{t(`pages.preview.nodes.${entry.type}`, entry.kind)}
							</Button>
						</div>
					))}
				</Group>

				<Group gap="xs">
					{isNarrow && hasNodeConfig ? (
						<Button
							variant="light"
							leftSection={<IconAdjustments size={16} />}
							onClick={openConfigDrawer}
							data-testid="preview-node-config-toggle"
						>
							{t("pages.preview.nodeConfig.open", "Block settings")}
						</Button>
					) : null}
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
				onChange={(event) => handleStartTextChange(event.currentTarget.value)}
				data-testid="preview-start-text"
			/>

			<Text size="xs" c="dimmed" data-testid="preview-delete-hint">
				{t(
					"pages.preview.deleteHint",
					"Tip: select a block or connection and press Delete (or Backspace) to remove it. Start and End cannot be removed.",
				)}
			</Text>

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
						onDrop={onDrop}
						onDragOver={onDragOver}
						fitView={true}
						deleteKeyCode={["Delete", "Backspace"]}
						proOptions={{ hideAttribution: true }}
						data-testid="preview-canvas-dropzone"
					>
						<Background />
						<Controls />
					</ReactFlow>
				</Paper>

				{!isNarrow && hasNodeConfig ? (
					<Paper
						withBorder={true}
						p="sm"
						style={{ width: "100%", maxWidth: 360 }}
						data-testid="preview-node-config"
					>
						{nodeConfig}
					</Paper>
				) : null}
			</Group>

			{/*
			 * Below the two-pane width the same form moves off-canvas instead of squeezing the canvas down to a sliver
			 * it cannot be edited in — the pattern ChatDisplayShell and the work-session detail page already use. The
			 * `preview-node-config` test id rides the content in both layouts, so a test (or an operator) finds the
			 * config in exactly one place whichever branch rendered.
			 */}
			{isNarrow ? (
				<Drawer
					opened={configDrawerOpened && hasNodeConfig}
					onClose={closeConfigDrawer}
					position="right"
					size="85%"
					title={t("pages.preview.nodeConfig.title", "Block settings")}
					// On `content`, not the root: Mantine spreads an unknown prop onto the zero-size portal root, which
					// Playwright reports as hidden. Same rule DialogShell applies.
					attributes={{ content: { "data-testid": "preview-node-config-drawer" } }}
					closeButtonProps={{ "aria-label": t("common.close", "Close") }}
				>
					<div data-testid="preview-node-config">{nodeConfig}</div>
				</Drawer>
			) : null}
		</Stack>
	);
}

export function WorkflowCanvas(props: WorkflowCanvasProps) {
	return (
		<ReactFlowProvider>
			<WorkflowCanvasInner {...props} />
		</ReactFlowProvider>
	);
}
