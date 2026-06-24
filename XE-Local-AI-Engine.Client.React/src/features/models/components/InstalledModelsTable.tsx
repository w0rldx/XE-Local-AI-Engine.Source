import { ActionIcon, Badge, Group, Stack, Table, Text, Tooltip } from "@mantine/core";
import { IconArrowBackUp, IconCheck, IconEye, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { LocalModelViewModel } from "@/features/models/models/LocalModelModel";
import { capabilityLabel, kindBadgeColor, kindLabel } from "@/features/models/models/ModelKindFormatters";

interface InstalledModelsTableProps {
	models: LocalModelViewModel[];
	isActionPending: boolean;
	onOpenDetails: (modelName: string) => void;
	onSetDefault: (modelName: string) => void;
	onDelete: (modelName: string) => void;
	onResetKind: (modelName: string) => void;
}

// Full-width presentation of the installed local models. The Type column is now read-only here (effective-kind badge,
// raw capability badges, and a quick reset-to-detected affordance for overridden models) — the editable override
// Select moved into the per-model details dialog so it no longer crowds the row. Clicking a model name opens that
// dialog; per-row Set-default and Delete actions stay in the Actions column.
export function InstalledModelsTable({
	models,
	isActionPending,
	onOpenDetails,
	onSetDefault,
	onDelete,
	onResetKind,
}: InstalledModelsTableProps) {
	const { t } = useTranslation();

	return (
		<Table.ScrollContainer minWidth={820}>
			<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="installed-models-table" data-tour="set-default-model">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>Name</Table.Th>
						<Table.Th>{t("pages.models.type.columnHeader", "Type")}</Table.Th>
						<Table.Th>Size</Table.Th>
						<Table.Th>Modified</Table.Th>
						<Table.Th>Family</Table.Th>
						<Table.Th>Quantization</Table.Th>
						<Table.Th>Actions</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{models.map((model) => (
						<Table.Tr key={model.modelName}>
							<Table.Td>
								<Group gap="xs" align="center" wrap="nowrap">
									<Text fw={500}>{model.modelName}</Text>
									{model.isSelected ? <Badge color="green">Default</Badge> : null}
								</Group>
							</Table.Td>
							<Table.Td>
								<Stack gap={6}>
									<Group gap={6} align="center">
										<Badge color={kindBadgeColor(model.kind)} variant="light" data-testid={`model-kind-badge-${model.modelName}`}>
											{kindLabel(t, model.kind)}
										</Badge>
										{model.isOverridden ? (
											<Tooltip label={t("pages.models.type.reset", "Reset to detected")} withArrow={true}>
												<ActionIcon
													aria-label={`Reset ${model.modelName} type to detected`}
													variant="subtle"
													color="gray"
													disabled={isActionPending}
													onClick={() => onResetKind(model.modelName)}
												>
													<IconArrowBackUp size={16} />
												</ActionIcon>
											</Tooltip>
										) : null}
									</Group>
									{model.capabilities.length > 0 ? (
										<Group gap={4}>
											{model.capabilities.map((capability) => (
												<Badge key={capability} size="xs" variant="outline" color="gray">
													{capabilityLabel(t, capability)}
												</Badge>
											))}
										</Group>
									) : null}
								</Stack>
							</Table.Td>
							<Table.Td>{model.sizeLabel}</Table.Td>
							<Table.Td>{model.modifiedDateLabel}</Table.Td>
							<Table.Td>{model.familyLabel}</Table.Td>
							<Table.Td>{model.quantizationLabel}</Table.Td>
							<Table.Td>
								<Group gap="xs">
									<Tooltip label="View details" withArrow={true}>
										<ActionIcon
											aria-label={`View ${model.modelName} details`}
											variant="subtle"
											data-testid={`model-details-button-${model.modelName}`}
											onClick={() => onOpenDetails(model.modelName)}
										>
											<IconEye size={16} />
										</ActionIcon>
									</Tooltip>
									<Tooltip label="Set as default model" withArrow={true}>
										<ActionIcon
											aria-label={`Set ${model.modelName} as default`}
											variant="subtle"
											color="green"
											disabled={isActionPending}
											onClick={() => onSetDefault(model.modelName)}
										>
											<IconCheck size={16} />
										</ActionIcon>
									</Tooltip>
									<Tooltip label="Delete model" withArrow={true}>
										<ActionIcon
											aria-label={`Delete ${model.modelName}`}
											variant="subtle"
											color="red"
											disabled={isActionPending}
											onClick={() => onDelete(model.modelName)}
										>
											<IconTrash size={16} />
										</ActionIcon>
									</Tooltip>
								</Group>
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}
