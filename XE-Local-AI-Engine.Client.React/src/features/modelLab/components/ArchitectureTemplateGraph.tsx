import { List, Stack, Text } from "@mantine/core";
import { Background, Controls, type Edge, type Node, ReactFlow } from "@xyflow/react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import "@xyflow/react/dist/style.css";

import type { ArchitectureTemplate } from "@/features/modelLab/models/ModelLabArchitecture";
import { layoutTemplate } from "@/features/modelLab/models/ModelLabTemplateLayout";

// Past this the canvas is unreadable at any zoom; the operations are listed in order instead.
const maxDrawnNodes = 80;

// The initial fit never zooms below this, so node text (12 px) opens at 9.6 px or more on a phone instead of about 3 px;
// a graph taller than the canvas is then panned. The controls still zoom out to the graph-wide minimum.
const fitViewOptions = { minZoom: 0.8 };

interface ArchitectureTemplateGraphProps {
	template: ArchitectureTemplate;
	// The block's components that have a tensor group; a node naming one selects that group.
	groupComponents: ReadonlySet<string>;
	selectedComponent: string | null;
	onSelectComponent: (component: string) => void;
}

const nodeStyle = {
	background: "var(--mantine-color-body)",
	color: "var(--mantine-color-text)",
	borderColor: "var(--mantine-color-default-border)",
	fontSize: 12,
};

// The block kind's operation order, strictly read-only: nothing here moves, connects or deletes. A pointer surface only;
// the tensor groups beside it are the keyboard path to the same selection.
export function ArchitectureTemplateGraph({
	template,
	groupComponents,
	selectedComponent,
	onSelectComponent,
}: ArchitectureTemplateGraphProps) {
	const { t } = useTranslation();
	// React Flow's own screen-reader text offers moving and deleting nodes, which this graph does not allow.
	const readOnlyDescription = t(
		"pages.modelLab.architecture.templateReadOnly",
		"Read-only graph. Select an operation with a pointer, or use the tensor groups to select by keyboard.",
	);
	const ariaLabelConfig = {
		"node.a11yDescription.default": readOnlyDescription,
		"node.a11yDescription.keyboardDisabled": readOnlyDescription,
		"edge.a11yDescription.default": readOnlyDescription,
	};
	const positions = useMemo(
		() =>
			layoutTemplate(
				template.nodes.map((node) => node.id),
				template.edges,
			),
		[template],
	);
	const nodes: Node[] = template.nodes.map((node) => {
		const linked = node.component !== null && groupComponents.has(node.component);
		return {
			id: node.id,
			position: positions.get(node.id) ?? { x: 0, y: 0 },
			data: { label: node.label },
			selected: linked && node.component === selectedComponent,
			style: {
				...nodeStyle,
				cursor: linked ? "pointer" : "default",
				borderStyle: linked ? "solid" : "dashed",
				outline: linked && node.component === selectedComponent ? "2px solid var(--mantine-primary-color-filled)" : undefined,
			},
		};
	});
	const edges: Edge[] = template.edges.map((edge) => ({
		id: `${edge.from}>${edge.to}`,
		source: edge.from,
		target: edge.to,
	}));

	if (nodes.length > maxDrawnNodes) {
		return (
			<List type="ordered" size="sm" data-testid="model-lab-architecture-template-list">
				{template.nodes.map((node) => (
					<List.Item key={node.id}>{node.label}</List.Item>
				))}
			</List>
		);
	}
	return (
		<Stack gap={4}>
			<div style={{ height: 420 }} data-testid="model-lab-architecture-template">
				<ReactFlow
					nodes={nodes}
					edges={edges}
					minZoom={0.1}
					nodesDraggable={false}
					nodesConnectable={false}
					edgesFocusable={false}
					elementsSelectable={false}
					fitView={true}
					fitViewOptions={fitViewOptions}
					proOptions={{ hideAttribution: true }}
					ariaLabelConfig={ariaLabelConfig}
					aria-label={t("pages.modelLab.architecture.templateLabel", "Operation graph of this block kind")}
					onNodeClick={(_event, node) => {
						const component = template.nodes.find((candidate) => candidate.id === node.id)?.component;
						if (component && groupComponents.has(component)) {
							onSelectComponent(component);
						}
					}}
				>
					<Background />
					<Controls showInteractive={false} />
				</ReactFlow>
			</div>
			<Text size="xs" c="dimmed">
				{t(
					"pages.modelLab.architecture.templateHint",
					"A solid node uses a tensor group of this block; selecting it selects that group. A dashed node holds no tensors.",
				)}
			</Text>
		</Stack>
	);
}
