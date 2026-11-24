import { Box, Button, Group, Stack, Text, UnstyledButton } from "@mantine/core";
import { IconArrowRight } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { type BlockKind, blockKindColors, blockKindLabels } from "@/core/models/inspect/GgufInspectInsights";
import {
	type ArchitectureBlock,
	type ArchitectureSelection,
	architectureBlockKind,
	componentLabel,
} from "@/features/modelLab/models/ModelLabArchitecture";
import { type ArchitectureHierarchy, type BlockChip, chipFor } from "@/features/modelLab/models/ModelLabHierarchy";
import { blockCellLabel, chipCounts, chipLabel } from "@/features/modelLab/models/ModelLabHierarchyLabels";

interface ArchitectureOverviewProps {
	hierarchy: ArchitectureHierarchy;
	// False when the file holds no `output` tensor of its own: the output may share the embedding weights.
	hasOutputTensor: boolean;
	// The file is one part of a split GGUF, so a missing output may be in another part.
	split: boolean;
	selection: ArchitectureSelection | null;
	// The blocks the chosen trace measured; null when no trace is chosen.
	tracedBlocks: ReadonlySet<number> | null;
	// The blocks the laid-over run recorded routing for; null when it has none to show.
	routedBlocks: ReadonlySet<number> | null;
	// Why the laid-over run of a routed architecture shows no routing: it recorded none (a 404) or it could not be read.
	// Said, not shown as zero; null when there is no failure to state.
	routingFailure: RoutingFailure | null;
	onSelect: (selection: ArchitectureSelection) => void;
}

const selectedOutline = "2px solid var(--mantine-color-text)";

export type RoutingFailure = "missing" | "unreadable";

// The groups outside the blocks, the blocks in this file as chips in index order with gap markers, and the MTP blocks
// apart. Every entry is a button with one action; the chip holding the selection shows its blocks.
export function ArchitectureOverview({
	hierarchy,
	hasOutputTensor,
	split,
	selection,
	tracedBlocks,
	routedBlocks,
	routingFailure,
	onSelect,
}: ArchitectureOverviewProps) {
	const { t } = useTranslation();
	const labels = blockKindLabels(t);
	const openChip = chipFor(hierarchy, selection);
	const mtpOpen = selection === "mtp" || hierarchy.mtp.some((block) => block.index === selection);
	const kinds = (Object.keys(blockKindColors) as BlockKind[]).filter((kind) =>
		[...hierarchy.trunkBlocks, ...hierarchy.mtp].some((block) => architectureBlockKind(block.kind) === kind),
	);
	const entry = (target: ArchitectureSelection, label: string) => (
		<Button
			key={target}
			variant={selection === target ? "filled" : "default"}
			size="compact-xs"
			aria-pressed={selection === target}
			onClick={() => onSelect(target)}
		>
			{label}
		</Button>
	);
	const blockCell = (block: ArchitectureBlock) => {
		const kind = architectureBlockKind(block.kind);
		const traced = tracedBlocks?.has(block.index) === true;
		const routed = routedBlocks?.has(block.index) === true;
		return (
			<UnstyledButton
				key={block.index}
				w={28}
				h={28}
				bg={blockKindColors[kind]}
				aria-label={blockCellLabel(t, block, tracedBlocks, routedBlocks)}
				aria-pressed={selection === block.index}
				onClick={() => onSelect(block.index)}
				data-testid={`model-lab-architecture-block-${block.index}`}
				data-traced={traced}
				data-routed={routed}
				style={{
					borderRadius: 4,
					position: "relative",
					outline: selection === block.index ? selectedOutline : undefined,
					outlineOffset: 1,
					opacity: block.kind === "mtp" ? 0.6 : undefined,
				}}
			>
				<Text size="xs" c="white" ta="center" fw={600}>
					{block.index}
				</Text>
				{traced && (
					<Box
						w={6}
						h={6}
						bg="white"
						aria-hidden="true"
						style={{ borderRadius: "50%", position: "absolute", top: 2, right: 2 }}
					/>
				)}
				{routed && <Box w={6} h={6} bg="white" aria-hidden="true" style={{ position: "absolute", bottom: 2, left: 2 }} />}
			</UnstyledButton>
		);
	};
	const chip = (item: BlockChip) => {
		if (item.blocks.length === 1 && item.blocks[0]) {
			return blockCell(item.blocks[0]);
		}
		const pressed = selection === item.selection;
		const counts = chipCounts(t, item, tracedBlocks, routedBlocks);
		return (
			<Group key={item.selection} gap={4} data-testid={`model-lab-architecture-chip-group-${item.selection}`}>
				<UnstyledButton
					px={6}
					py={2}
					aria-pressed={pressed}
					onClick={() => onSelect(item.selection)}
					data-testid={`model-lab-architecture-chip-${item.selection}`}
					style={{
						border: "1px solid var(--mantine-color-default-border)",
						borderRadius: 4,
						outline: pressed ? selectedOutline : undefined,
						outlineOffset: 1,
					}}
				>
					<Group gap={4}>
						{item.runs.map((run, at) => (
							<Box
								// biome-ignore lint/suspicious/noArrayIndexKey: runs repeat kinds, so a run's place is its only identity.
								key={at}
								w={8}
								h={8}
								bg={blockKindColors[architectureBlockKind(run.kind)]}
								aria-hidden="true"
								style={{ borderRadius: 2 }}
							/>
						))}
						<Text size="xs" fw={600}>
							{chipLabel(t, item)}
						</Text>
						{counts.length > 0 && (
							<Text size="xs" c="dimmed">
								{`· ${counts.join(" · ")}`}
							</Text>
						)}
					</Group>
				</UnstyledButton>
				{openChip === item && item.blocks.map(blockCell)}
			</Group>
		);
	};
	const gap = (key: string) => (
		<Text key={key} size="xs" c="dimmed" data-testid="model-lab-architecture-gap">
			{t("pages.modelLab.architecture.hierarchy.gap", "… blocks not in this file …")}
		</Text>
	);
	// Embedding → Blocks → Output, each only when the file holds it.
	const ends = [
		hierarchy.outside.includes("embedding") && entry("embedding", componentLabel(t, "embedding")),
		hierarchy.trunkBlocks.length > 0 &&
			entry(
				"stack",
				t("pages.modelLab.architecture.hierarchy.blocksEntry", "Blocks: {{count}} in this file", {
					count: hierarchy.trunkBlocks.length,
				}),
			),
		hierarchy.outside.includes("output") && entry("output", componentLabel(t, "output")),
	].filter((element) => element !== false);

	return (
		<Stack gap="xs" data-testid="model-lab-architecture-overview">
			<Group gap={4} align="center">
				{ends.flatMap((element) =>
					element === ends[0] ? [element] : [<IconArrowRight key={`to-${element.key}`} size={14} aria-hidden="true" />, element],
				)}
				{hierarchy.mtp.length > 0 && (
					<Group gap={4} ml="sm">
						{entry("mtp", t("pages.models.inspect.blockKinds.mtp", "MTP"))}
						{mtpOpen && hierarchy.mtp.map(blockCell)}
					</Group>
				)}
				{hierarchy.outside.includes("other") && <Box ml="sm">{entry("other", componentLabel(t, "other"))}</Box>}
			</Group>
			{hierarchy.segments.length > 0 && (
				<Group gap={6} align="center" data-testid="model-lab-architecture-chips">
					{hierarchy.segments.flatMap((segment, at) => [
						...(segment.gapBefore ? [gap(`gap-${at}`)] : []),
						...segment.chips.map(chip),
					])}
					{hierarchy.gapAfter && gap("gap-end")}
				</Group>
			)}
			{!hasOutputTensor && (
				<Text size="xs" c="dimmed" data-testid="model-lab-architecture-no-output">
					{split
						? t(
								"pages.modelLab.architecture.noOutputSplit",
								"No separate output tensor in this file; the output may share the embedding weights, or it is in another part of the split GGUF.",
							)
						: t(
								"pages.modelLab.architecture.noOutput",
								"No separate output tensor in this file; the output may share the embedding weights.",
							)}
				</Text>
			)}
			<Group gap="md">
				{kinds.map((kind) => (
					<Group key={kind} gap={6} wrap="nowrap">
						<Box w={8} h={8} bg={blockKindColors[kind]} style={{ borderRadius: 2 }} aria-hidden="true" />
						<Text size="xs">
							{kind === "mtp"
								? t("pages.modelLab.architecture.kindUntraceable", "{{kind}} (not traceable)", { kind: labels[kind] })
								: labels[kind]}
						</Text>
					</Group>
				))}
				{tracedBlocks && (
					<Text size="xs" c="dimmed">
						{t("pages.modelLab.architecture.tracedLegend", "A dot marks a traced block.")}
					</Text>
				)}
				{routedBlocks && (
					<Text size="xs" c="dimmed">
						{t("pages.modelLab.architecture.routedLegend", "A square marks a block whose routing the run recorded.")}
					</Text>
				)}
				{routingFailure === "missing" && (
					<Text size="xs" c="dimmed" data-testid="model-lab-architecture-routing-missing">
						{t("pages.modelLab.architecture.routingMissing", "The laid-over run recorded no routing.")}
					</Text>
				)}
				{routingFailure === "unreadable" && (
					<Text size="xs" c="dimmed" data-testid="model-lab-architecture-routing-failed">
						{t("pages.modelLab.architecture.routingFailed", "The run's routing could not be read.")}
					</Text>
				)}
			</Group>
		</Stack>
	);
}
