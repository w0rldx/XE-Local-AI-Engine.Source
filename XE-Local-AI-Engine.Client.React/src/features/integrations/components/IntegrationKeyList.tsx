import { ActionIcon, Badge, Code, Group, Table, Text, Tooltip } from "@mantine/core";
import { IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { shortPrincipalId } from "@/features/integrations/components/IntegrationFormatters";
import type { IntegrationApiKey, IntegrationTrigger } from "@/features/integrations/models/IntegrationModels";

interface IntegrationKeyListProps {
	keys: readonly IntegrationApiKey[];
	triggers: readonly IntegrationTrigger[];
	isMutating: boolean;
	onRevoke: (key: IntegrationApiKey) => void;
}

// Table of integration API keys. `principalId` gets its own column because it is the stable integrator identity —
// sessions, executions and the rate-limit partition all key on it, so two rows sharing a principal are one
// integrator with two credentials and must be comparable at a glance. A revoked row is dimmed and loses its action.
export function IntegrationKeyList({ keys, triggers, isMutating, onRevoke }: IntegrationKeyListProps) {
	const { t } = useTranslation();

	if (keys.length === 0) {
		return (
			<EmptyState
				message={t("pages.integrations.keys.list.empty", "No API keys yet. Generate one to let an integrator connect.")}
				data-testid="integration-keys-empty"
			/>
		);
	}

	const triggerLabel = (id: string): string => triggers.find((trigger) => trigger.id === id)?.displayName ?? id;

	return (
		<Table.ScrollContainer minWidth={980}>
			<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="integration-keys-table">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.integrations.keys.list.columns.prefix", "Key")}</Table.Th>
						<Table.Th>{t("pages.integrations.keys.list.columns.principal", "Principal")}</Table.Th>
						<Table.Th>{t("pages.integrations.keys.list.columns.label", "Label")}</Table.Th>
						<Table.Th>{t("pages.integrations.keys.list.columns.triggers", "Allowed triggers")}</Table.Th>
						<Table.Th>{t("pages.integrations.keys.list.columns.created", "Created")}</Table.Th>
						<Table.Th>{t("pages.integrations.keys.list.columns.lastUsed", "Last used")}</Table.Th>
						<Table.Th>{t("pages.integrations.keys.list.columns.revoked", "Revoked")}</Table.Th>
						<Table.Th>{t("pages.integrations.keys.list.columns.actions", "Actions")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{keys.map((key) => (
						<Table.Tr
							key={key.id}
							opacity={key.revokedAtUtc === null ? 1 : 0.55}
							data-testid={`integration-key-row-${key.id}`}
						>
							<Table.Td>
								<Text size="sm" ff="monospace">
									{`${key.keyPrefix}…`}
								</Text>
							</Table.Td>
							<Table.Td>
								<Tooltip label={key.principalId}>
									<Code data-testid={`integration-key-principal-${key.id}`} title={key.principalId}>
										{shortPrincipalId(key.principalId)}
									</Code>
								</Tooltip>
							</Table.Td>
							<Table.Td>
								<Text fw={600}>{key.label}</Text>
							</Table.Td>
							<Table.Td>
								{key.allowedTriggerIds === null ? (
									<Badge variant="light" color="orange">
										{t("pages.integrations.keys.list.allTriggers", "All triggers")}
									</Badge>
								) : (
									<Group gap={4}>
										{key.allowedTriggerIds.map((id) => (
											<Badge key={id} variant="outline" color="grape">
												{triggerLabel(id)}
											</Badge>
										))}
									</Group>
								)}
							</Table.Td>
							<Table.Td>
								<Text size="sm">{formatTimestamp(key.createdAtUtc)}</Text>
							</Table.Td>
							<Table.Td>
								<Text size="sm" c={key.lastUsedAtUtc === null ? "dimmed" : undefined}>
									{key.lastUsedAtUtc === null
										? t("pages.integrations.keys.list.neverUsed", "Never used yet")
										: formatTimestamp(key.lastUsedAtUtc)}
								</Text>
							</Table.Td>
							<Table.Td>
								{key.revokedAtUtc === null ? (
									<Text size="sm" c="dimmed">
										—
									</Text>
								) : (
									<Badge variant="light" color="red" data-testid={`integration-key-revoked-${key.id}`}>
										{t("pages.integrations.keys.list.revokedBadge", "Revoked")}
									</Badge>
								)}
							</Table.Td>
							<Table.Td>
								{key.revokedAtUtc === null ? (
									<ActionIcon
										aria-label={t("pages.integrations.keys.list.revokeAria", "Revoke {{label}}", { label: key.label })}
										variant="subtle"
										color="red"
										disabled={isMutating}
										onClick={() => onRevoke(key)}
										data-testid={`integration-key-revoke-${key.id}`}
									>
										<IconTrash size={16} />
									</ActionIcon>
								) : null}
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}
