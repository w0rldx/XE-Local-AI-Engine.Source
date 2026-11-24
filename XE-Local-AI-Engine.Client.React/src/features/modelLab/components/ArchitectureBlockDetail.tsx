import { Group, Paper, Stack, Text } from "@mantine/core";
import { useReducedMotion } from "@mantine/hooks";
import { useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { blockKindLabels } from "@/core/models/inspect/GgufInspectInsights";
import { ArchitectureBlockRouting } from "@/features/modelLab/components/ArchitectureBlockRouting";
import { ArchitectureBlockTrace } from "@/features/modelLab/components/ArchitectureBlockTrace";
import {
	BasisBadge,
	ExplanationEntry,
	ExplanationLevelSwitch,
	ExplanationList,
} from "@/features/modelLab/components/ArchitectureExplanation";
import { ArchitecturePyTorchPanel } from "@/features/modelLab/components/ArchitecturePyTorchPanel";
import { ArchitectureTemplateGraph } from "@/features/modelLab/components/ArchitectureTemplateGraph";
import { ArchitectureTensorDetails } from "@/features/modelLab/components/ArchitectureTensorDetails";
import { ArchitectureTensorGroups } from "@/features/modelLab/components/ArchitectureTensorGroups";
import {
	type ArchitectureBlock,
	type ArchitectureGroup,
	type ArchitectureTemplate,
	architectureBlockKind,
	type BlockTraceSummary,
	componentIdFor,
	componentLabel,
	type OutsideEnd,
} from "@/features/modelLab/models/ModelLabArchitecture";
import { type ExplanationLevel, operationCandidates } from "@/features/modelLab/models/ModelLabExplanation";
import type { PyTorchCodeTarget } from "@/features/modelLab/models/ModelLabPyTorch";

interface ArchitectureBlockDetailProps {
	modelName: string;
	// The GGUF `general.architecture`: its explanation texts come before the shared ones. Null when the file names none.
	architecture: string | null;
	suggestedBaseRepo: string | null;
	// Undefined for the tensors outside every block: then `outside` names which end of the model they are.
	block: ArchitectureBlock | undefined;
	outside: OutsideEnd;
	groups: ArchitectureGroup[];
	// Undefined when this support level or block kind has none: no graph is ever drawn without one.
	template: ArchitectureTemplate | undefined;
	traceSummaries: ReadonlyMap<number, BlockTraceSummary> | undefined;
	// The trace laid over the blocks, null while none is chosen.
	traceId: string | null;
	// The block is in the explorer's one routed set (saved steps of a readable routing that name it), the same set the row
	// and graph mark; the routing panel shows for nothing else.
	routed: boolean;
	// Owned above this panel, so the chosen level stays while the operator moves between blocks.
	explanationLevel: ExplanationLevel;
	onExplanationLevel: (level: ExplanationLevel) => void;
	// Takes focus when this detail was opened from a control that unmounted with the last one.
	headingRef?: (element: HTMLElement | null) => void;
}

// One block: what the file holds (read) and, apart from it, the operation order llama.cpp runs over it (inferred).
export function ArchitectureBlockDetail({
	modelName,
	architecture,
	suggestedBaseRepo,
	block,
	outside,
	groups,
	template,
	traceSummaries,
	traceId,
	routed,
	explanationLevel,
	onExplanationLevel,
	headingRef,
}: ArchitectureBlockDetailProps) {
	const { t } = useTranslation();
	const [selectedComponentId, setSelectedComponentId] = useState<string | null>(null);
	const [selectedTensorName, setSelectedTensorName] = useState<string | null>(null);
	const [codeTarget, setCodeTarget] = useState<PyTorchCodeTarget | null>(null);
	const [openNodeId, setOpenNodeId] = useState<string | null>(null);
	const operationsRef = useRef<HTMLDivElement>(null);
	const reduceMotion = useReducedMotion();
	// The list sits below the graph, often below the fold: without the scroll a node click looks like it did nothing.
	// Centred, not "nearest": nearest leaves the item's header on the bottom edge and its opening panel still out of view.
	const openFromGraph = (nodeId: string): void => {
		setOpenNodeId(nodeId);
		const items = operationsRef.current?.querySelectorAll<HTMLElement>("[data-item-id]") ?? [];
		[...items]
			.find((item) => item.dataset["itemId"] === nodeId)
			?.scrollIntoView({ block: "center", behavior: reduceMotion ? "auto" : "smooth" });
	};
	const selectedTensor = groups.flatMap((group) => group.tensors).find((tensor) => tensor.ggufName === selectedTensorName);
	const selectedGroup = groups.find((group) => group.componentId === selectedComponentId);
	const selectGroup = (componentId: string): void => {
		setSelectedComponentId(componentId);
		setSelectedTensorName(null);
		setCodeTarget({ kind: "component", componentId });
	};

	return (
		<Paper withBorder={true} p="md" data-testid="model-lab-architecture-detail">
			<Stack gap="sm">
				<Text component="h3" fw={600} tabIndex={-1} ref={headingRef} data-testid="model-lab-architecture-detail-heading">
					{block === undefined
						? componentLabel(t, outside)
						: t("pages.models.inspect.blockCell", "Block {{index}}: {{kind}}", {
								index: block.index,
								kind: blockKindLabels(t)[architectureBlockKind(block.kind)],
							})}
				</Text>
				<ExplanationLevelSwitch value={explanationLevel} onChange={onExplanationLevel} />
				{/* A block's kind text describes the graph llama.cpp builds, so it is inferred wherever that graph is shown. */}
				<ExplanationEntry
					candidates={[block === undefined ? `component.${outside}` : `block.${architectureBlockKind(block.kind)}`]}
					level={explanationLevel}
					basis={block && template ? { inferredFrom: template.source } : "interpreted"}
				/>
				{block && (
					<ArchitectureBlockTrace
						untraceable={block.kind === "mtp"}
						summaries={traceSummaries}
						blockIndex={block.index}
						level={explanationLevel}
					/>
				)}
				{/* Apart from the residual summary: a block can have routing whatever the trace records say about it. */}
				{block && traceId !== null && routed && <ArchitectureBlockRouting traceId={traceId} blockIndex={block.index} />}
				<Group gap="xs">
					<Text size="sm" fw={600}>
						{t("pages.modelLab.architecture.groups", "Tensor groups")}
					</Text>
					<BasisBadge basis="structural" data-testid="model-lab-architecture-read-badge" />
				</Group>
				<ArchitectureTensorGroups
					groups={groups}
					blockIndex={block?.index}
					selectedComponentId={selectedComponentId}
					selectedTensorName={selectedTensorName}
					onSelectGroup={selectGroup}
					onSelectTensor={(componentId, ggufName) => {
						setSelectedComponentId(componentId);
						setSelectedTensorName(ggufName);
						setCodeTarget({ kind: "component", componentId });
					}}
				/>
				{selectedTensor && <ArchitectureTensorDetails tensor={selectedTensor} />}
				{selectedGroup && (
					<ExplanationEntry
						title={componentLabel(t, selectedGroup.component)}
						candidates={[`component.${selectedGroup.component}`]}
						level={explanationLevel}
						basis="interpreted"
					/>
				)}
				{block && template && (
					<>
						<Group gap="xs">
							<Text size="sm" fw={600}>
								{t("pages.modelLab.architecture.operations", "Operations")}
							</Text>
							<BasisBadge basis={{ inferredFrom: template.source }} data-testid="model-lab-architecture-inferred-badge" />
						</Group>
						<ArchitectureTemplateGraph
							template={template}
							groupComponents={new Set(groups.map((group) => group.component))}
							selectedComponent={selectedGroup?.component ?? null}
							onSelectComponent={(component) => selectGroup(componentIdFor(block.index, component))}
							onSelectNode={openFromGraph}
						/>
						<ExplanationList
							items={template.nodes.map((node) => ({
								id: node.id,
								label: node.label,
								candidates: operationCandidates(template.kind, architecture, node.id),
							}))}
							level={explanationLevel}
							basis={{ inferredFrom: template.source }}
							open={openNodeId}
							onOpen={setOpenNodeId}
							data-testid="model-lab-explain-operations"
							ref={operationsRef}
						/>
					</>
				)}
				<ArchitecturePyTorchPanel
					modelName={modelName}
					suggestedBaseRepo={suggestedBaseRepo}
					block={block}
					target={codeTarget}
					onTarget={setCodeTarget}
				/>
			</Stack>
		</Paper>
	);
}
