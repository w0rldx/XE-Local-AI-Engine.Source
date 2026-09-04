import { ActionIcon, Badge, Group, Switch, Table, Text } from "@mantine/core";
import { IconPencil, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import type { IntegrationAgentOption, IntegrationTrigger } from "@/features/integrations/models/IntegrationModels";

interface IntegrationTriggerListProps {
	triggers: readonly IntegrationTrigger[];
	agents: readonly IntegrationAgentOption[];
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
	isMutating,
	onEdit,
	onDelete,
	onToggleEnabled,
}: IntegrationTriggerListProps) {
	const { t } = useTranslation();

	if (triggers.length === 0) {
		return (
			<EmptyState
				message={t("pages.integrations.triggers.list.empty", "No integration triggers yet. Create one to get started.")}
				data-testid="integration-triggers-empty"
			/>
		);
	}

	return (
		<Table.ScrollContainer minWidth={880}>
			<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="integration-triggers-table">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.integrations.triggers.list.columns.name", "Name")}</Table.Th>
						<Table.Th>{t("pages.integrations.triggers.list.columns.displayName", "Display name")}</Table.Th>
						<Table.Th>{t("pages.integrations.triggers.list.columns.target", "Target agent")}</Table.Th>
						<Table.Th>{t("pages.integrations.triggers.list.columns.sessionPolicy", "Session policy")}</Table.Th>
						<Table.Th>{t("pages.integrations.triggers.list.columns.inputs", "Accepted inputs")}</Table.Th>
						<Table.Th>{t("pages.integrations.triggers.list.columns.enabled", "Enabled")}</Table.Th>
						<Table.Th>{t("pages.integrations.triggers.list.columns.actions", "Actions")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{triggers.map((trigger) => (
						<Table.Tr key={trigger.id} data-testid={`integration-trigger-row-${trigger.id}`}>
							<Table.Td>
								<Text size="sm" ff="monospace">
									{trigger.name}
								</Text>
							</Table.Td>
							<Table.Td>
								<Text fw={600}>{trigger.displayName}</Text>
								{trigger.description ? (
									<Text size="xs" c="dimmed" lineClamp={1}>
										{trigger.description}
									</Text>
								) : null}
							</Table.Td>
							<Table.Td>
								<Text size="sm">
									{agents.find((agent) => agent.id === trigger.targetAgentDefinitionId)?.name ?? trigger.targetAgentDefinitionId}
								</Text>
							</Table.Td>
							<Table.Td>
								<Badge variant="light" color="blue">
									{t(
										`pages.integrations.triggers.form.sessionPolicy.options.${trigger.sessionPolicy}`,
										trigger.sessionPolicy,
									)}
								</Badge>
							</Table.Td>
							<Table.Td>
								<Group gap={4}>
									{trigger.acceptedInputKinds.map((kind) => (
										<Badge key={kind} variant="outline" color="grape">
											{/* The same two labels the editor's checkboxes use, so the row and the form never disagree. */}
											{t(
												`pages.integrations.triggers.form.acceptedInputs.options.${kind === "json" ? "Json" : "Text"}`,
												kind,
											)}
										</Badge>
									))}
								</Group>
							</Table.Td>
							<Table.Td>
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
							</Table.Td>
							<Table.Td>
								<Group gap="xs">
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
								</Group>
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}
