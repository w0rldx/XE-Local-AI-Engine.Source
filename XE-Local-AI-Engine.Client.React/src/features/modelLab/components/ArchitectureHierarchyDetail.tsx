import { Alert, Button, Group, Paper, Stack, Text } from "@mantine/core";
import { Fragment, useState } from "react";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientServicesModelLabModelLabArchitectureHyperparameters as Hyperparameters } from "@/core/api/generated/types.gen";
import { blockKindLabels } from "@/core/models/inspect/GgufInspectInsights";
import { BasisBadge, ExplanationLevelSwitch, ExplanationList } from "@/features/modelLab/components/ArchitectureExplanation";
import { type ArchitectureValue, ArchitectureValues } from "@/features/modelLab/components/ArchitectureValues";
import {
	type ArchitectureSelection,
	type ArchitectureTemplate,
	architectureBlockKind,
	componentLabel,
	formatBlockRanges,
} from "@/features/modelLab/models/ModelLabArchitecture";
import { type ExplanationLevel, operationCandidates } from "@/features/modelLab/models/ModelLabExplanation";
import {
	type ArchitectureHierarchy,
	allChips,
	type BlockChip,
	compareChipBlocks,
} from "@/features/modelLab/models/ModelLabHierarchy";
import { type Crumb, chipLabel } from "@/features/modelLab/models/ModelLabHierarchyLabels";

type Select = (selection: ArchitectureSelection | null) => void;
// The heading a selection made from a detail control moves focus to.
type HeadingRef = (element: HTMLElement | null) => void;

interface ArchitectureBreadcrumbProps {
	crumbs: readonly Crumb[];
	onOpen: Select;
}

// Plain buttons up the hierarchy; the last item is where the operator is.
export function ArchitectureBreadcrumb({ crumbs, onOpen }: ArchitectureBreadcrumbProps) {
	const { t } = useTranslation();
	return (
		<Group
			component="nav"
			gap={4}
			aria-label={t("pages.modelLab.architecture.hierarchy.breadcrumb", "Where you are in the model")}
			data-testid="model-lab-architecture-breadcrumb"
		>
			{crumbs.map((crumb, at) => (
				<Fragment key={String(crumb.selection)}>
					{at > 0 && (
						<Text size="sm" c="dimmed" aria-hidden="true">
							›
						</Text>
					)}
					{at === crumbs.length - 1 ? (
						<Text size="sm" fw={600} aria-current="location">
							{crumb.label}
						</Text>
					) : (
						<Button variant="subtle" size="compact-sm" onClick={() => onOpen(crumb.selection)}>
							{crumb.label}
						</Button>
					)}
				</Fragment>
			))}
		</Group>
	);
}

function DetailHeading({ headingRef, children }: { headingRef: HeadingRef; children: string }) {
	return (
		<Text component="h3" fw={600} tabIndex={-1} ref={headingRef} data-testid="model-lab-architecture-detail-heading">
			{children}
		</Text>
	);
}

interface RootDetailProps {
	hierarchy: ArchitectureHierarchy;
	values: Hyperparameters | null;
	headingRef: HeadingRef;
}

// The whole model: the header's values, what lies outside the blocks and how many blocks this file holds.
export function ArchitectureRootDetail({ hierarchy, values, headingRef }: RootDetailProps) {
	const { t } = useTranslation();
	const items: ArchitectureValue[] = [
		{ key: "fileType", value: values?.fileType, basis: "structural" },
		{ key: "declaredParamCount", value: values?.declaredParamCount, basis: "structural" },
		{ key: "blockCount", value: values?.blockCount, basis: "structural" },
		{ key: "nextnPredictLayers", value: values?.nextnPredictLayers, basis: "structural" },
		{ key: "blocksInFile", value: hierarchy.trunkBlocks.length, basis: "derived" },
		{ key: "mtpInFile", value: hierarchy.mtp.length > 0 ? hierarchy.mtp.length : null, basis: "derived" },
		{ key: "contextLength", value: values?.contextLength, basis: "structural" },
		{ key: "embeddingLength", value: values?.embeddingLength, basis: "structural" },
	];
	return (
		<Paper withBorder={true} p="md" data-testid="model-lab-architecture-root">
			<Stack gap="sm">
				<DetailHeading headingRef={headingRef}>
					{t("pages.modelLab.architecture.hierarchy.rootTitle", "Whole model")}
				</DetailHeading>
				<ArchitectureValues values={items} mtpDeclared={values?.nextnPredictLayers != null} />
				{values?.splitCount != null && values.splitCount > 1 && (
					<Alert color="blue" data-testid="model-lab-architecture-split">
						{t(
							"pages.modelLab.architecture.hierarchy.split",
							"This file is one of the {{count}} parts of a split GGUF. The blocks counted here are the ones in this file.",
							{ count: values.splitCount },
						)}
					</Alert>
				)}
				{hierarchy.outside.length > 0 && (
					<Text size="sm" data-testid="model-lab-architecture-root-outside">
						{t("pages.modelLab.architecture.hierarchy.outside", "Outside the blocks: {{groups}}.", {
							groups: hierarchy.outside.map((end) => componentLabel(t, end)).join(", "),
						})}
					</Text>
				)}
			</Stack>
		</Paper>
	);
}

// The head counts of a full-attention block.
function attentionValues(values: Hyperparameters | null): ArchitectureValue[] {
	return [
		{ key: "attentionHeadCount", value: values?.attentionHeadCount, basis: "structural" },
		{ key: "attentionHeadCountKv", value: values?.attentionHeadCountKv, basis: "structural" },
	];
}

interface StackDetailProps {
	hierarchy: ArchitectureHierarchy;
	values: Hyperparameters | null;
	headingRef: HeadingRef;
	onOpen: Select;
}

// Every block in this file: counts per kind, the repeating pattern when the trunk is whole, and the chips.
export function ArchitectureStackDetail({ hierarchy, values, headingRef, onOpen }: StackDetailProps) {
	const { t } = useTranslation();
	const labels = blockKindLabels(t);
	const counts = new Map<string, number>();
	for (const block of hierarchy.trunkBlocks) {
		const kind = labels[architectureBlockKind(block.kind)];
		counts.set(kind, (counts.get(kind) ?? 0) + 1);
	}
	const experts = values?.expertCount != null && values.expertCount > 0;
	const items: ArchitectureValue[] = [
		{ key: "blocksInFile", value: hierarchy.trunkBlocks.length, basis: "derived" },
		...attentionValues(values),
		{ key: "attentionKeyLength", value: values?.attentionKeyLength, basis: "structural" },
		{ key: "attentionValueLength", value: values?.attentionValueLength, basis: "structural" },
		{ key: "fullAttentionInterval", value: values?.fullAttentionInterval, basis: "structural" },
		{ key: "expertCount", value: experts ? values?.expertCount : null, basis: "structural" },
		{ key: "expertUsedCount", value: experts ? values?.expertUsedCount : null, basis: "structural" },
	];
	const pattern = hierarchy.pattern;
	return (
		<Paper withBorder={true} p="md" data-testid="model-lab-architecture-stack">
			<Stack gap="sm">
				<DetailHeading headingRef={headingRef}>
					{t("pages.modelLab.architecture.hierarchy.stackTitle", "Blocks in this file")}
				</DetailHeading>
				<Group gap="xs">
					<Text size="sm" data-testid="model-lab-architecture-kind-counts">
						{[...counts].map(([kind, count]) => `${kind}: ${count}`).join(" · ")}
					</Text>
					<BasisBadge basis="derived" />
				</Group>
				{pattern && (
					<Group gap="xs">
						<Text size="sm" data-testid="model-lab-architecture-pattern">
							{t("pages.modelLab.architecture.hierarchy.pattern", "{{unit}}, {{count}} times", {
								unit: pattern.unit
									.map((run) =>
										t("pages.modelLab.architecture.hierarchy.patternRun", "{{count}} × {{kind}}", {
											count: run.count,
											kind: labels[architectureBlockKind(run.kind)],
										}),
									)
									.join(" + "),
								count: pattern.repeats,
							})}
						</Text>
						<BasisBadge basis="derived" />
					</Group>
				)}
				<ArchitectureValues values={items} mtpDeclared={values?.nextnPredictLayers != null} />
				<Group gap="xs">
					{allChips(hierarchy).map((chip) => (
						<Button key={chip.selection} variant="default" size="compact-sm" onClick={() => onOpen(chip.selection)}>
							{chip.blocks.length === 1
								? t("pages.modelLab.architecture.hierarchy.block", "Block {{index}}", { index: chip.first })
								: chipLabel(t, chip)}
						</Button>
					))}
				</Group>
			</Stack>
		</Paper>
	);
}

function OpenBlocks({ indices, onOpen }: { indices: readonly number[]; onOpen: Select }) {
	const { t } = useTranslation();
	return (
		<Group gap="xs" data-testid="model-lab-architecture-open-blocks">
			{indices.map((index) => (
				<Button key={index} variant="default" size="compact-sm" onClick={() => onOpen(index)}>
					{t("pages.modelLab.architecture.hierarchy.openBlock", "Open block {{index}}", { index })}
				</Button>
			))}
		</Group>
	);
}

function KindOperations({
	template,
	architecture,
	level,
}: {
	template: ArchitectureTemplate;
	architecture: string | null;
	level: ExplanationLevel;
}) {
	const { t } = useTranslation();
	const [open, setOpen] = useState<string | null>(null);
	return (
		<Stack gap={4}>
			<Group gap="xs">
				<Text size="sm" fw={600}>
					{t("pages.modelLab.architecture.hierarchy.kindOperations", "Operations of a {{kind}} block", {
						kind: blockKindLabels(t)[architectureBlockKind(template.kind)],
					})}
				</Text>
				<BasisBadge basis={{ inferredFrom: template.source }} />
			</Group>
			<ExplanationList
				items={template.nodes.map((node) => ({
					id: node.id,
					label: node.label,
					candidates: operationCandidates(template.kind, architecture, node.id),
				}))}
				level={level}
				basis={{ inferredFrom: template.source }}
				open={open}
				onOpen={setOpen}
				data-testid={`model-lab-architecture-range-operations-${template.kind}`}
			/>
		</Stack>
	);
}

interface RangeDetailProps {
	chip: BlockChip;
	architecture: string | null;
	// Only a `detailed` file's: no graph or text is ever inferred for another.
	templates: readonly ArchitectureTemplate[];
	values: Hyperparameters | null;
	tracedBlocks: ReadonlySet<number> | null;
	routedBlocks: ReadonlySet<number> | null;
	level: ExplanationLevel;
	onLevelChange: (level: ExplanationLevel) => void;
	headingRef: HeadingRef;
	onOpen: Select;
}

// A chip of blocks: what they are and share, and the operations of each kind as text. No graph, no tensor actions.
export function ArchitectureRangeDetail({
	chip,
	architecture,
	templates,
	values,
	tracedBlocks,
	routedBlocks,
	level,
	onLevelChange,
	headingRef,
	onOpen,
}: RangeDetailProps) {
	const { t } = useTranslation();
	const labels = blockKindLabels(t);
	const comparison = compareChipBlocks(chip.blocks);
	const indices = chip.blocks.map((block) => block.index);
	const kinds = [...new Set(chip.blocks.map((block) => block.kind))];
	const inChip = (set: ReadonlySet<number> | null) => (set === null ? null : indices.filter((index) => set.has(index)));
	const traced = inChip(tracedBlocks);
	const routed = inChip(routedBlocks);
	return (
		<Paper withBorder={true} p="md" data-testid="model-lab-architecture-range">
			<Stack gap="sm">
				<DetailHeading headingRef={headingRef}>
					{t("pages.modelLab.architecture.hierarchy.rangeTitle", "Blocks {{first}}–{{last}}", {
						first: chip.first,
						last: chip.last,
					})}
				</DetailHeading>
				<Text size="sm" data-testid="model-lab-architecture-range-kinds">
					{t("pages.modelLab.architecture.hierarchy.rangeKinds", "{{count}} blocks: {{runs}}", {
						count: chip.blocks.length,
						runs: chip.runs
							.map((run) =>
								t("pages.modelLab.architecture.hierarchy.chipRun", "{{kind}} ×{{count}}", {
									kind: labels[architectureBlockKind(run.kind)],
									count: run.count,
								}),
							)
							.join(" + "),
					})}
				</Text>
				{traced && (
					<Text size="sm" data-testid="model-lab-architecture-range-traced">
						{t("pages.modelLab.architecture.hierarchy.rangeTraced", "Traced in the laid-over run: {{blocks}}", {
							blocks: formatBlockRanges(traced) || "–",
						})}
					</Text>
				)}
				{routed && (
					<Text size="sm" data-testid="model-lab-architecture-range-routed">
						{t("pages.modelLab.architecture.hierarchy.rangeRouted", "Routing recorded in the laid-over run: {{blocks}}", {
							blocks: formatBlockRanges(routed) || "–",
						})}
					</Text>
				)}
				<Group gap="xs">
					<Stack gap={2}>
						<Text size="sm" data-testid="model-lab-architecture-range-layout">
							{comparison.layoutCompared === 0
								? t(
										"pages.modelLab.architecture.hierarchy.layoutNothing",
										"Nothing in this group to compare: no two blocks are of one kind.",
									)
								: comparison.layoutDiffers.length === 0
									? t(
											"pages.modelLab.architecture.hierarchy.layoutSame",
											"Blocks of one kind hold the same tensors: component, name and shape.",
										)
									: t(
											"pages.modelLab.architecture.hierarchy.layoutDiffers",
											"The tensors differ from the first block of the same kind at blocks {{blocks}}.",
											{ blocks: formatBlockRanges(comparison.layoutDiffers) },
										)}
						</Text>
						<Text size="sm" data-testid="model-lab-architecture-range-types">
							{comparison.typesCompared === 0
								? t(
										"pages.modelLab.architecture.hierarchy.typesNothing",
										"Nothing in this group to compare: no tensors correspond between blocks of one kind.",
									)
								: comparison.typeUnknown
									? t(
											"pages.modelLab.architecture.hierarchy.typesUnknown",
											"A stored type is unknown, so the stored types are not compared.",
										)
									: comparison.typesDiffer.length === 0
										? t("pages.modelLab.architecture.hierarchy.typesSame", "Corresponding tensors use the same stored types.")
										: t(
												"pages.modelLab.architecture.hierarchy.typesDiffer",
												"Stored types of corresponding tensors differ at blocks {{blocks}}.",
												{ blocks: formatBlockRanges(comparison.typesDiffer) },
											)}
						</Text>
					</Stack>
					<BasisBadge basis="derived" />
				</Group>
				{kinds.includes("fullAttention") && (
					<ArchitectureValues values={attentionValues(values)} mtpDeclared={values?.nextnPredictLayers != null} />
				)}
				{kinds.some((kind) => templates.some((template) => template.kind === kind)) && (
					<ExplanationLevelSwitch value={level} onChange={onLevelChange} />
				)}
				{kinds.flatMap((kind) => {
					const template = templates.find((candidate) => candidate.kind === kind);
					return template ? [<KindOperations key={kind} template={template} architecture={architecture} level={level} />] : [];
				})}
				<OpenBlocks indices={indices} onOpen={onOpen} />
			</Stack>
		</Paper>
	);
}

interface MtpDetailProps {
	hierarchy: ArchitectureHierarchy;
	values: Hyperparameters | null;
	headingRef: HeadingRef;
	onOpen: Select;
}

// The multi-token prediction blocks: a separate path, never part of the main stack.
export function ArchitectureMtpDetail({ hierarchy, values, headingRef, onOpen }: MtpDetailProps) {
	const { t } = useTranslation();
	return (
		<Paper withBorder={true} p="md" data-testid="model-lab-architecture-mtp">
			<Stack gap="sm">
				<DetailHeading headingRef={headingRef}>{componentLabel(t, "mtp")}</DetailHeading>
				<Text size="sm" data-testid="model-lab-architecture-mtp-note">
					{t(
						"pages.modelLab.architecture.hierarchy.mtpNote",
						"These blocks predict further tokens on a separate path beside the main stack; they are not one of its blocks.",
					)}
				</Text>
				<ArchitectureValues
					values={[
						{ key: "nextnPredictLayers", value: values?.nextnPredictLayers, basis: "structural" },
						{ key: "mtpInFile", value: hierarchy.mtp.length, basis: "derived" },
					]}
					mtpDeclared={true}
				/>
				<OpenBlocks indices={hierarchy.mtp.map((block) => block.index)} onOpen={onOpen} />
			</Stack>
		</Paper>
	);
}
