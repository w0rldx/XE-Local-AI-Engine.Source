import { Button, Stack, Table, Text } from "@mantine/core";
import { Background, Controls, type Edge, type Node, ReactFlow } from "@xyflow/react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import "@xyflow/react/dist/style.css";

import { nodeLocation, type PyTorchGraph } from "@/features/modelLab/models/ModelLabPyTorch";
import { layoutTemplate, RANK_SPACING_Y } from "@/features/modelLab/models/ModelLabTemplateLayout";

// Past this many nodes the canvas is unreadable at any zoom; the operations are listed instead.
const maxDrawnNodes = 80;

// The first fit frames the selected node, else the graph's first rows, never below 0.6 (12 px text at 7 px or more) nor
// above 1: a block's whole graph fitted at once opened at about 0.14. The controls still zoom out to the whole graph.
const openRows = 3;
const openZoom = { minZoom: 0.6, maxZoom: 1 };

// Four lines keep a node inside its 90 px row; the source viewer shows the whole line.
const labelStyle = { display: "-webkit-box", WebkitLineClamp: 4, WebkitBoxOrient: "vertical", overflow: "hidden" } as const;

const nodeStyle = {
	background: "var(--mantine-color-body)",
	color: "var(--mantine-color-text)",
	borderColor: "var(--mantine-color-default-border)",
	fontSize: 12,
	width: 200,
};

interface ArchitecturePyTorchGraphProps {
	graph: PyTorchGraph;
	// The class whose own forward an op with an empty module path ran in: the block's, or the model's outside every block.
	rootName: string;
	selectedNodeId: string | null;
	onSelectNode: (nodeId: string) => void;
}

const nodeText = (modulePath: string | undefined, label: string | undefined, rootName: string): string =>
	`${modulePath || rootName}: ${label ?? ""}`;

// The ops torch.export recorded for this block, grouped by module and source line. Read-only, as the template graph is.
export function ArchitecturePyTorchGraph({ graph, rootName, selectedNodeId, onSelectNode }: ArchitecturePyTorchGraphProps) {
	const { t } = useTranslation();
	const graphNodes = graph.nodes ?? [];
	const readOnlyDescription = t(
		"pages.modelLab.pytorch.graphReadOnly",
		"Read-only graph. Select an operation with a pointer to open its source line, or use the module list to select by keyboard.",
	);
	const ariaLabelConfig = {
		"node.a11yDescription.default": readOnlyDescription,
		"node.a11yDescription.keyboardDisabled": readOnlyDescription,
		"edge.a11yDescription.default": readOnlyDescription,
	};
	const edgePairs = useMemo(() => (graph.edges ?? []).flatMap(([from, to]) => (from && to ? [{ from, to }] : [])), [graph]);
	const positions = useMemo(
		() =>
			layoutTemplate(
				(graph.nodes ?? []).map((node) => node.id ?? ""),
				edgePairs,
			),
		[graph, edgePairs],
	);

	if (graphNodes.length > maxDrawnNodes) {
		return (
			<Table fz="xs" data-testid="model-lab-pytorch-graph-list">
				<Table.Tbody>
					{graphNodes.map((node) => (
						<Table.Tr key={node.id}>
							<Table.Td>
								<Button
									variant={node.id === selectedNodeId ? "light" : "subtle"}
									size="compact-xs"
									ff="monospace"
									disabled={nodeLocation(node) === null}
									onClick={() => onSelectNode(node.id ?? "")}
								>
									{nodeText(node.modulePath, node.label, rootName)}
								</Button>
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		);
	}

	const nodes: Node[] = graphNodes.map((node) => {
		const linked = nodeLocation(node) !== null;
		const selected = linked && node.id === selectedNodeId;
		return {
			id: node.id ?? "",
			position: positions.get(node.id ?? "") ?? { x: 0, y: 0 },
			data: { label: <span style={labelStyle}>{nodeText(node.modulePath, node.label, rootName)}</span> },
			selected,
			style: {
				...nodeStyle,
				cursor: linked ? "pointer" : "default",
				borderStyle: linked ? "solid" : "dashed",
				outline: selected ? "2px solid var(--mantine-primary-color-filled)" : undefined,
			},
		};
	});
	const edges: Edge[] = edgePairs.map((edge) => ({ id: `${edge.from}>${edge.to}`, source: edge.from, target: edge.to }));
	const openOn =
		selectedNodeId !== null && positions.has(selectedNodeId)
			? [{ id: selectedNodeId }]
			: nodes.filter((node) => node.position.y < openRows * RANK_SPACING_Y).map((node) => ({ id: node.id }));

	return (
		<Stack gap={4}>
			<div style={{ height: 420 }} data-testid="model-lab-pytorch-graph">
				<ReactFlow
					// Another block's graph opens with its own first fit.
					key={graph.graphId}
					nodes={nodes}
					edges={edges}
					minZoom={0.1}
					nodesDraggable={false}
					nodesConnectable={false}
					edgesFocusable={false}
					elementsSelectable={false}
					fitView={true}
					fitViewOptions={{ ...openZoom, nodes: openOn }}
					proOptions={{ hideAttribution: true }}
					ariaLabelConfig={ariaLabelConfig}
					aria-label={t("pages.modelLab.pytorch.graphLabel", "PyTorch operation graph of this block")}
					onNodeClick={(_event, clicked) => {
						if (nodeLocation(graphNodes.find((node) => node.id === clicked.id)) !== null) {
							onSelectNode(clicked.id);
						}
					}}
				>
					<Background />
					<Controls showInteractive={false} />
				</ReactFlow>
			</div>
			<Text size="xs" c="dimmed">
				{t(
					"pages.modelLab.pytorch.graphHint",
					"A solid node opens its source line. A dashed node has no recorded line and opens nothing.",
				)}
			</Text>
		</Stack>
	);
}
