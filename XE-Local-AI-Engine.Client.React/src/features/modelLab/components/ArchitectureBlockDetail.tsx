import { Badge, Group, Paper, Stack, Text } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { blockKindLabels } from "@/core/models/inspect/GgufInspectInsights";
import { ArchitectureBlockTrace } from "@/features/modelLab/components/ArchitectureBlockTrace";
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
} from "@/features/modelLab/models/ModelLabArchitecture";

interface ArchitectureBlockDetailProps {
	// Undefined for the tensors outside every block.
	block: ArchitectureBlock | undefined;
	groups: ArchitectureGroup[];
	// Undefined when this support level or block kind has none: no graph is ever drawn without one.
	template: ArchitectureTemplate | undefined;
	traceSummaries: ReadonlyMap<number, BlockTraceSummary> | undefined;
}

const inferredBadgeStyles = {
	root: { height: "auto" },
	label: { whiteSpace: "normal", overflow: "visible", overflowWrap: "anywhere" },
} as const;

// One block: what the file holds (read) and, apart from it, the operation order llama.cpp runs over it (inferred).
export function ArchitectureBlockDetail({ block, groups, template, traceSummaries }: ArchitectureBlockDetailProps) {
	const { t } = useTranslation();
	const [selectedComponentId, setSelectedComponentId] = useState<string | null>(null);
	const [selectedTensorName, setSelectedTensorName] = useState<string | null>(null);
	const selectedTensor = groups.flatMap((group) => group.tensors).find((tensor) => tensor.ggufName === selectedTensorName);
	const selectedGroup = groups.find((group) => group.componentId === selectedComponentId);
	const selectGroup = (componentId: string): void => {
		setSelectedComponentId(componentId);
		setSelectedTensorName(null);
	};

	return (
		<Paper withBorder={true} p="md" data-testid="model-lab-architecture-detail">
			<Stack gap="sm">
				<Text fw={600}>
					{block === undefined
						? t("pages.models.inspect.nonBlock", "Outside blocks")
						: t("pages.models.inspect.blockCell", "Block {{index}}: {{kind}}", {
								index: block.index,
								kind: blockKindLabels(t)[architectureBlockKind(block.kind)],
							})}
				</Text>
				{block && (
					<ArchitectureBlockTrace untraceable={block.kind === "mtp"} summaries={traceSummaries} blockIndex={block.index} />
				)}
				<Group gap="xs">
					<Text size="sm" fw={600}>
						{t("pages.modelLab.architecture.groups", "Tensor groups")}
					</Text>
					<Badge variant="light" color="green" data-testid="model-lab-architecture-read-badge">
						{t("pages.modelLab.architecture.readBadge", "Read from the file")}
					</Badge>
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
					}}
				/>
				{selectedTensor && <ArchitectureTensorDetails tensor={selectedTensor} />}
				{block && template && (
					<>
						<Group gap="xs">
							<Text size="sm" fw={600}>
								{t("pages.modelLab.architecture.operations", "Operations")}
							</Text>
							{/* Wraps instead of clipping: the pinned llama.cpp tag at its end is the part that matters. */}
							<Badge
								variant="light"
								color="orange"
								maw="100%"
								styles={inferredBadgeStyles}
								data-testid="model-lab-architecture-inferred-badge"
							>
								{t("pages.modelLab.architecture.inferredBadge", "Inferred from llama.cpp ({{source}})", {
									source: template.source,
								})}
							</Badge>
						</Group>
						<ArchitectureTemplateGraph
							template={template}
							groupComponents={new Set(groups.map((group) => group.component))}
							selectedComponent={selectedGroup?.component ?? null}
							onSelectComponent={(component) => selectGroup(componentIdFor(block.index, component))}
						/>
					</>
				)}
			</Stack>
		</Paper>
	);
}
