import { ActionIcon, Badge, Code, Group, Text, Tooltip } from "@mantine/core";
import { IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { ResponsiveTable } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
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

	const triggerLabel = (id: string): string => triggers.find((trigger) => trigger.id === id)?.displayName ?? id;

	return (
		<ResponsiveTable
			rows={keys}
			rowKey={(key) => key.id}
			rowTestId={(key) => `integration-key-row-${key.id}`}
			rowStyle={(key) => ({ opacity: key.revokedAtUtc === null ? 1 : 0.55 })}
			minWidth={980}
			data-testid="integration-keys-table"
			empty={
				<EmptyState
					message={t("pages.integrations.keys.list.empty", "No API keys yet. Generate one to let an integrator connect.")}
					data-testid="integration-keys-empty"
				/>
			}
			columns={[
				{
					key: "prefix",
					header: t("pages.integrations.keys.list.columns.prefix", "Key"),
					cell: (key) => (
						<Text size="sm" ff="monospace">
							{`${key.keyPrefix}…`}
						</Text>
					),
				},
				{
					key: "principal",
					header: t("pages.integrations.keys.list.columns.principal", "Principal"),
					cell: (key) => (
						<Tooltip label={key.principalId}>
							<Code data-testid={`integration-key-principal-${key.id}`} title={key.principalId}>
								{shortPrincipalId(key.principalId)}
							</Code>
						</Tooltip>
					),
				},
				{
					key: "label",
					header: t("pages.integrations.keys.list.columns.label", "Label"),
					primary: true,
					wrap: true,
					cell: (key) => <Text fw={600}>{key.label}</Text>,
				},
				{
					key: "triggers",
					header: t("pages.integrations.keys.list.columns.triggers", "Allowed triggers"),
					cell: (key) =>
						key.allowedTriggerIds === null ? (
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
						),
				},
				{
					key: "created",
					header: t("pages.integrations.keys.list.columns.created", "Created"),
					cell: (key) => <Text size="sm">{formatTimestamp(key.createdAtUtc)}</Text>,
				},
				{
					key: "lastUsed",
					header: t("pages.integrations.keys.list.columns.lastUsed", "Last used"),
					cell: (key) => (
						<Text size="sm" c={key.lastUsedAtUtc === null ? "dimmed" : undefined}>
							{key.lastUsedAtUtc === null
								? t("pages.integrations.keys.list.neverUsed", "Never used yet")
								: formatTimestamp(key.lastUsedAtUtc)}
						</Text>
					),
				},
				{
					key: "revoked",
					header: t("pages.integrations.keys.list.columns.revoked", "Revoked"),
					cell: (key) =>
						key.revokedAtUtc === null ? (
							<Text size="sm" c="dimmed">
								—
							</Text>
						) : (
							<Badge variant="light" color="red" data-testid={`integration-key-revoked-${key.id}`}>
								{t("pages.integrations.keys.list.revokedBadge", "Revoked")}
							</Badge>
						),
				},
			]}
			actionsHeader={t("pages.integrations.keys.list.columns.actions", "Actions")}
			actions={(key) =>
				key.revokedAtUtc === null ? (
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
				) : null
			}
		/>
	);
}
