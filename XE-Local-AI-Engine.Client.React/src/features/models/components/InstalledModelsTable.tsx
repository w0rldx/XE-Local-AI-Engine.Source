import { ActionIcon, Badge, Group, Stack, Text, Tooltip } from "@mantine/core";
import { IconArrowBackUp, IconCheck, IconEye, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { ResponsiveTable, type ResponsiveTableColumn } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
import type { LocalModelViewModel } from "@/features/models/models/LocalModelModel";
import { capabilityBadgeColor, capabilityLabel, kindBadgeColor, kindLabel } from "@/features/models/models/ModelKindFormatters";

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

	const columns: readonly ResponsiveTableColumn<LocalModelViewModel>[] = [
		{
			key: "name",
			header: t("pages.models.local.columns.name", "Name"),
			primary: true,
			cell: (model) => (
				<Group gap="xs" align="center">
					<Text fw={500} style={{ wordBreak: "break-word" }}>
						{model.modelName}
					</Text>
					{model.isSelected ? <Badge color="green">{t("pages.models.local.defaultBadge", "Default")}</Badge> : null}
					{model.origin === "imported" ? (
						<Badge color="violet" variant="light">
							{t("pages.models.local.origin.imported", "Imported")}
						</Badge>
					) : null}
				</Group>
			),
		},
		{
			key: "type",
			header: t("pages.models.type.columnHeader", "Type"),
			cell: (model) => (
				<Stack gap={6}>
					<Group gap={6} align="center">
						<Badge color={kindBadgeColor(model.kind)} variant="light" data-testid={`model-kind-badge-${model.modelName}`}>
							{kindLabel(t, model.kind)}
						</Badge>
						{model.isOverridden ? (
							<Tooltip label={t("pages.models.type.reset", "Reset to detected")} withArrow={true}>
								<ActionIcon
									aria-label={t("pages.models.type.resetAria", "Reset {{name}} type to detected", {
										name: model.modelName,
									})}
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
								<Badge key={capability} size="xs" variant="outline" color={capabilityBadgeColor(capability)}>
									{capabilityLabel(t, capability)}
								</Badge>
							))}
						</Group>
					) : null}
				</Stack>
			),
		},
		{ key: "size", header: t("pages.models.local.columns.size", "Size"), cell: (model) => model.sizeLabel },
		{ key: "modified", header: t("pages.models.local.columns.modified", "Modified"), cell: (model) => model.modifiedDateLabel },
		{ key: "family", header: t("pages.models.local.columns.family", "Family"), cell: (model) => model.familyLabel },
		{
			key: "quantization",
			header: t("pages.models.local.columns.quantization", "Quantization"),
			cell: (model) => model.quantizationLabel,
		},
	];

	return (
		<ResponsiveTable
			rows={models}
			columns={columns}
			rowKey={(model) => model.modelName}
			minWidth={820}
			empty={<EmptyState message={t("pages.models.local.empty", "No local models found.")} />}
			data-testid="installed-models-table"
			data-tour="set-default-model"
			actionsHeader={t("pages.models.local.columns.actions", "Actions")}
			actions={(model) => (
				<>
					<Tooltip label={t("pages.models.local.actions.viewDetails", "View details")} withArrow={true}>
						<ActionIcon
							aria-label={t("pages.models.local.actions.viewDetailsAria", "View {{name}} details", {
								name: model.modelName,
							})}
							variant="subtle"
							data-testid={`model-details-button-${model.modelName}`}
							onClick={() => onOpenDetails(model.modelName)}
						>
							<IconEye size={16} />
						</ActionIcon>
					</Tooltip>
					<Tooltip label={t("pages.models.local.actions.setDefault", "Set as default model")} withArrow={true}>
						<ActionIcon
							aria-label={t("pages.models.local.actions.setDefaultAria", "Set {{name}} as default", {
								name: model.modelName,
							})}
							variant="subtle"
							color="green"
							disabled={isActionPending}
							onClick={() => onSetDefault(model.modelName)}
						>
							<IconCheck size={16} />
						</ActionIcon>
					</Tooltip>
					<Tooltip label={t("pages.models.local.delete.title", "Delete model")} withArrow={true}>
						<ActionIcon
							aria-label={t("pages.models.local.actions.deleteAria", "Delete {{name}}", { name: model.modelName })}
							variant="subtle"
							color="red"
							disabled={isActionPending}
							onClick={() => onDelete(model.modelName)}
						>
							<IconTrash size={16} />
						</ActionIcon>
					</Tooltip>
				</>
			)}
		/>
	);
}
