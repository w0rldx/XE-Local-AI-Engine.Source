import { ActionIcon, Badge, Group, Switch, Text } from "@mantine/core";
import { IconPencil, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { ResponsiveTable } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
import type {
	IntegrationAgentOption,
	IntegrationGraphOption,
	IntegrationTrigger,
} from "@/features/integrations/models/IntegrationModels";

interface IntegrationTriggerListProps {
	triggers: readonly IntegrationTrigger[];
	agents: readonly IntegrationAgentOption[];
	graphs: readonly IntegrationGraphOption[];
	isMutating: boolean;
	onEdit: (id: string) => void;
	onDelete: (trigger: IntegrationTrigger) => void;
	onToggleEnabled: (trigger: IntegrationTrigger, enabled: boolean) => void;
}

// Table of integration triggers with an enable switch, edit and delete row actions. Pure presentation — the page
// owns the data and the handlers.
export function IntegrationTriggerList({
	triggers,
	agents,
	graphs,
	isMutating,
	onEdit,
	onDelete,
	onToggleEnabled,
}: IntegrationTriggerListProps) {
	const { t } = useTranslation();

	// The target's name for either kind; the raw id when the target has since been deleted.
	const targetName = (trigger: IntegrationTrigger): string =>
		trigger.targetKind === "Graph"
			? (graphs.find((graph) => graph.id === trigger.targetGraphWorkflowDefinitionId)?.name ??
				trigger.targetGraphWorkflowDefinitionId ??
				"")
			: (agents.find((agent) => agent.id === trigger.targetAgentDefinitionId)?.name ?? trigger.targetAgentDefinitionId ?? "");

	return (
		<ResponsiveTable
			rows={triggers}
			rowKey={(trigger) => trigger.id}
			rowTestId={(trigger) => `integration-trigger-row-${trigger.id}`}
			minWidth={880}
			data-testid="integration-triggers-table"
			empty={
				<EmptyState
					message={t("pages.integrations.triggers.list.empty", "No integration triggers yet. Create one to get started.")}
					data-testid="integration-triggers-empty"
				/>
			}
			columns={[
				{
					key: "name",
					header: t("pages.integrations.triggers.list.columns.name", "Name"),
					cell: (trigger) => (
						<Text size="sm" ff="monospace">
							{trigger.name}
						</Text>
					),
				},
				{
					key: "displayName",
					header: t("pages.integrations.triggers.list.columns.displayName", "Display name"),
					primary: true,
					cell: (trigger) => (
						<>
							<Text fw={600}>{trigger.displayName}</Text>
							{trigger.description ? (
								<Text size="xs" c="dimmed" lineClamp={1}>
									{trigger.description}
								</Text>
							) : null}
						</>
					),
				},
				{
					key: "target",
					header: t("pages.integrations.triggers.list.columns.target", "Target"),
					cell: (trigger) => (
						<Group gap={6} wrap="nowrap">
							<Badge variant="light" color={trigger.targetKind === "Graph" ? "teal" : "gray"} size="sm">
								{t(
									`pages.integrations.triggers.form.targetKind.options.${trigger.targetKind}`,
									trigger.targetKind === "Graph" ? "Graph workflow" : "Agent",
								)}
							</Badge>
							<Text
								size="sm"
								style={{ minWidth: 0, overflowWrap: "anywhere" }}
								data-testid={`integration-trigger-target-${trigger.id}`}
							>
								{targetName(trigger)}
							</Text>
						</Group>
					),
				},
				{
					key: "sessionPolicy",
					header: t("pages.integrations.triggers.list.columns.sessionPolicy", "Session policy"),
					cell: (trigger) => (
						<Badge variant="light" color="blue">
							{t(`pages.integrations.triggers.form.sessionPolicy.options.${trigger.sessionPolicy}`, trigger.sessionPolicy)}
						</Badge>
					),
				},
				{
					key: "inputs",
					header: t("pages.integrations.triggers.list.columns.inputs", "Accepted inputs"),
					cell: (trigger) => (
						<Group gap={4}>
							{trigger.acceptedInputKinds.map((kind) => (
								<Badge key={kind} variant="outline" color="grape">
									{/* The same two labels the editor's checkboxes use, so the row and the form never disagree. */}
									{t(`pages.integrations.triggers.form.acceptedInputs.options.${kind === "json" ? "Json" : "Text"}`, kind)}
								</Badge>
							))}
						</Group>
					),
				},
				{
					key: "enabled",
					header: t("pages.integrations.triggers.list.columns.enabled", "Enabled"),
					cell: (trigger) => (
						<Switch
							size="sm"
							checked={trigger.enabled}
							disabled={isMutating}
							onChange={(event) => onToggleEnabled(trigger, event.currentTarget.checked)}
							aria-label={t("pages.integrations.triggers.list.enabledAria", "Toggle {{name}}", {
								name: trigger.displayName,
							})}
							data-testid={`integration-trigger-enabled-${trigger.id}`}
						/>
					),
				},
			]}
			actionsHeader={t("pages.integrations.triggers.list.columns.actions", "Actions")}
			actions={(trigger) => (
				<>
					<ActionIcon
						aria-label={t("pages.integrations.triggers.list.editAria", "Edit {{name}}", {
							name: trigger.displayName,
						})}
						variant="subtle"
						disabled={isMutating}
						onClick={() => onEdit(trigger.id)}
						data-testid={`integration-trigger-edit-${trigger.id}`}
					>
						<IconPencil size={16} />
					</ActionIcon>
					<ActionIcon
						aria-label={t("pages.integrations.triggers.list.deleteAria", "Delete {{name}}", {
							name: trigger.displayName,
						})}
						variant="subtle"
						color="red"
						disabled={isMutating}
						onClick={() => onDelete(trigger)}
						data-testid={`integration-trigger-delete-${trigger.id}`}
					>
						<IconTrash size={16} />
					</ActionIcon>
				</>
			)}
		/>
	);
}
