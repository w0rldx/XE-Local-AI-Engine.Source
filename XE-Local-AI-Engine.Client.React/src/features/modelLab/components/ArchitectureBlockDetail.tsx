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
import { type ExplanationLevel, operationCandidates, operationLabel } from "@/features/modelLab/models/ModelLabExplanation";
import type { ArchitectureDetailPatch } from "@/features/modelLab/models/ModelLabNavigation";
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
	// The open operation, tensor group and tensor, from the URL; a value naming nothing here is ignored. Each gesture
	// writes one patch, and `onShown` runs only once the page shows it: never for a blocked change.
	selection: ArchitectureDetailPatch;
	// Changes each time the tab shows this detail again: the open operation then scrolls into view once.
	arrival: number;
	// Runs that scroll once the router has restored its own scroll positions, which would otherwise cancel it.
	afterNavigation: (run: () => void) => void;
	onSelect: (patch: ArchitectureDetailPatch, onShown?: () => void) => void;
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
	selection,
	arrival,
	afterNavigation,
	onSelect,
	explanationLevel,
	onExplanationLevel,
	headingRef,
}: ArchitectureBlockDetailProps) {
	const { t, i18n } = useTranslation();
	// Read at once, so an arrival's scroll at mount already honours it.
	const reduceMotion = useReducedMotion(false, { getInitialValueInEffect: false });
	// The tensor, when it names one here, decides the group; else the group, when it names one.
	const tensorGroup = groups.find((group) => group.tensors.some((tensor) => tensor.ggufName === selection.tensor));
	const selectedGroup = tensorGroup ?? groups.find((group) => group.componentId === selection.group);
	const selectedTensor = tensorGroup?.tensors.find((tensor) => tensor.ggufName === selection.tensor);
	const openNodeId = template?.nodes.some((node) => node.id === selection.op) ? (selection.op ?? null) : null;
	// The code panel follows the selected group, unless the operator picked a module or node there for this selection.
	const shownSelection = JSON.stringify([selectedGroup?.componentId, selectedTensor?.ggufName]);
	const [pick, setPick] = useState<{ target: PyTorchCodeTarget; forSelection: string } | null>(null);
	const codeTarget: PyTorchCodeTarget | null =
		pick?.forSelection === shownSelection
			? pick.target
			: selectedGroup
				? { kind: "component", componentId: selectedGroup.componentId }
				: null;
	const clearPick = (): void => setPick(null);
	const operations = useRef<HTMLDivElement | null>(null);
	// A link, reload or Back that opens this detail on an operation scrolls to it once, as the list mounts. The tab showing
	// this kept detail again swaps in a fresh callback, which React attaches in the commit that shows the panel. The
	// scroll itself waits until the router has restored the scroller for a Back, which would cancel it otherwise.
	const scrollOnAttach = (nodeId: string | null) => {
		let pending = nodeId;
		return (element: HTMLDivElement | null): void => {
			operations.current = element;
			if (element !== null && pending !== null) {
				const target = pending;
				afterNavigation(() => scrollToOperation(element, target, reduceMotion));
				pending = null;
			}
		};
	};
	const [attachOperations, setAttachOperations] = useState(() => ({ arrival, ref: scrollOnAttach(openNodeId) }));
	if (attachOperations.arrival !== arrival) {
		setAttachOperations({ arrival, ref: scrollOnAttach(openNodeId) });
	}
	// A graph pick scrolls once the page shows it, again on each repeated pick; an accordion or keyboard open never does.
	const pickNode = (nodeId: string, component: string | null): void => {
		const scroll = () => scrollToOperation(operations.current, nodeId, reduceMotion);
		if (component === null || block === undefined) {
			onSelect({ op: nodeId }, scroll);
			return;
		}
		onSelect({ op: nodeId, group: componentIdFor(block.index, component), tensor: undefined }, () => {
			clearPick();
			scroll();
		});
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
					selectedComponentId={selectedGroup?.componentId ?? null}
					selectedTensorName={selectedTensor?.ggufName ?? null}
					onSelectGroup={(componentId) => onSelect({ group: componentId, tensor: undefined }, clearPick)}
					onSelectTensor={(componentId, ggufName) => onSelect({ group: componentId, tensor: ggufName }, clearPick)}
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
							architecture={architecture}
							groupComponents={new Set(groups.map((group) => group.component))}
							selectedComponent={selectedGroup?.component ?? null}
							onSelectNode={pickNode}
						/>
						<ExplanationList
							items={template.nodes.map((node) => ({
								id: node.id,
								label: operationLabel(i18n, template, architecture, node),
								candidates: operationCandidates(template.kind, architecture, node.id),
							}))}
							level={explanationLevel}
							basis={{ inferredFrom: template.source }}
							open={openNodeId}
							onOpen={(nodeId) => onSelect({ op: nodeId ?? undefined })}
							data-testid="model-lab-explain-operations"
							ref={attachOperations.ref}
						/>
					</>
				)}
				<ArchitecturePyTorchPanel
					modelName={modelName}
					suggestedBaseRepo={suggestedBaseRepo}
					block={block}
					target={codeTarget}
					onTarget={(target) => setPick({ target, forSelection: shownSelection })}
				/>
			</Stack>
		</Paper>
	);
}

// The list sits below the graph, often below the fold: without the scroll a node click looks like it did nothing.
// Centred, not "nearest": nearest leaves the item's header on the bottom edge and its opening panel still out of view.
function scrollToOperation(list: HTMLElement | null, nodeId: string, reduceMotion: boolean): void {
	const items = list?.querySelectorAll<HTMLElement>("[data-item-id]") ?? [];
	[...items]
		.find((item) => item.dataset["itemId"] === nodeId)
		?.scrollIntoView({ block: "center", behavior: reduceMotion ? "auto" : "smooth" });
}
