import { Box, Button, Group, Stack, Text, UnstyledButton } from "@mantine/core";
import { IconArrowRight } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { type BlockKind, blockKindColors, blockKindLabels } from "@/core/models/inspect/GgufInspectInsights";
import {
	type ArchitectureBlock,
	type ArchitectureSelection,
	architectureBlockKind,
} from "@/features/modelLab/models/ModelLabArchitecture";

interface ArchitectureOverviewProps {
	blocks: ArchitectureBlock[];
	// False when the file holds no `output` tensor of its own, so there is no Output entry to open.
	hasOutput: boolean;
	selection: ArchitectureSelection | null;
	// The blocks the chosen trace measured; null when no trace is chosen.
	tracedBlocks: ReadonlySet<number> | null;
	onSelect: (selection: ArchitectureSelection) => void;
}

const selectedOutline = "2px solid var(--mantine-color-text)";

// Embedding, then every block in file order, then the output: each block a button, so the strip is the keyboard path too.
export function ArchitectureOverview({ blocks, hasOutput, selection, tracedBlocks, onSelect }: ArchitectureOverviewProps) {
	const { t } = useTranslation();
	const labels = blockKindLabels(t);
	const cellLabel = (block: ArchitectureBlock): string => {
		const values = { index: block.index, kind: labels[architectureBlockKind(block.kind)] };
		if (block.kind === "mtp") {
			return t("pages.modelLab.architecture.blockUntraceable", "Block {{index}}: {{kind}}, not traceable", values);
		}
		return tracedBlocks?.has(block.index)
			? t("pages.modelLab.architecture.blockTraced", "Block {{index}}: {{kind}}, traced", values)
			: t("pages.models.inspect.blockCell", "Block {{index}}: {{kind}}", values);
	};
	const kinds = (Object.keys(blockKindColors) as BlockKind[]).filter((kind) =>
		blocks.some((block) => architectureBlockKind(block.kind) === kind),
	);
	const outside = (label: string) => (
		<Button
			variant={selection === "outside" ? "filled" : "default"}
			size="compact-xs"
			aria-pressed={selection === "outside"}
			onClick={() => onSelect("outside")}
		>
			{label}
		</Button>
	);

	return (
		<Stack gap="xs" data-testid="model-lab-architecture-overview">
			<Group gap={4} align="center">
				{outside(t("pages.modelLab.architecture.component.embedding", "Embedding"))}
				<IconArrowRight size={14} aria-hidden="true" />
				{blocks.map((block) => {
					const kind = architectureBlockKind(block.kind);
					const traced = tracedBlocks?.has(block.index) === true;
					return (
						<UnstyledButton
							key={block.index}
							w={28}
							h={28}
							bg={blockKindColors[kind]}
							aria-label={cellLabel(block)}
							aria-pressed={selection === block.index}
							onClick={() => onSelect(block.index)}
							data-testid={`model-lab-architecture-block-${block.index}`}
							data-traced={traced}
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
						</UnstyledButton>
					);
				})}
				{hasOutput && (
					<>
						<IconArrowRight size={14} aria-hidden="true" />
						{outside(t("pages.modelLab.architecture.component.output", "Output"))}
					</>
				)}
			</Group>
			{!hasOutput && (
				<Text size="xs" c="dimmed" data-testid="model-lab-architecture-no-output">
					{t(
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
			</Group>
		</Stack>
	);
}
