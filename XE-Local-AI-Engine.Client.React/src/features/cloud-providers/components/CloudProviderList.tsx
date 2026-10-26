import { Badge, Button, Group, Stack, Text } from "@mantine/core";
import { IconPencil } from "@tabler/icons-react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import type { ExternalProviderConnectionDto } from "@/features/cloud-providers/external/models/ExternalProviderFormState";
import { parseCloudGrants, parseLocality } from "@/features/cloud-providers/external/models/ExternalProviderModel";

export interface AzureFoundrySummary {
	readonly endpoint: string | null;
	readonly modelCount: number;
}

export interface CodexSummary {
	readonly accountId: string | null;
}

interface CloudProviderListProps {
	readonly connections: readonly ExternalProviderConnectionDto[];
	// `null` when the slot is empty: an unconfigured Azure Foundry or a signed-out Codex is not listed.
	readonly azureFoundry: AzureFoundrySummary | null;
	readonly codex: CodexSummary | null;
	readonly addAction: ReactNode;
	readonly onEditExternal: (connectionId: string) => void;
	readonly onEditAzureFoundry: () => void;
	readonly onEditCodex: () => void;
}

// Every configured provider on one list, whatever store backs it: the `ext:` connections, the Azure Foundry slot and
// the Codex session.
export function CloudProviderList(props: CloudProviderListProps) {
	const { connections, azureFoundry, codex, onEditExternal } = props;
	const { t } = useTranslation();
	const isEmpty = connections.length === 0 && azureFoundry === null && codex === null;

	return (
		<SectionCard
			title={t("pages.cloudProviders.list.title", "Configured providers")}
			actions={props.addAction}
			data-testid="cloud-provider-list"
		>
			{isEmpty ? (
				<EmptyState message={t("pages.cloudProviders.list.empty")} data-testid="cloud-provider-list-empty" />
			) : (
				<Stack gap="sm">
					{azureFoundry === null ? null : (
						<ProviderRow
							name={t("pages.cloudProviders.kind.azureFoundry", "Azure AI Foundry")}
							detail={azureFoundry.endpoint}
							status={t("pages.externalProviders.list.modelCount", { count: azureFoundry.modelCount })}
							testId="cloud-provider-azure-foundry"
							onEdit={props.onEditAzureFoundry}
						/>
					)}
					{codex === null ? null : (
						<ProviderRow
							name={t("pages.cloudProviders.kind.codex", "ChatGPT (Codex)")}
							detail={codex.accountId}
							status={t("pages.cloudSettings.codex.badge.signedIn")}
							testId="cloud-provider-codex"
							onEdit={props.onEditCodex}
						/>
					)}
					{connections.map((connection) => {
						const locality = parseLocality(connection.locality);
						const modelCount = connection.models?.length ?? 0;
						const grantCount = Object.values(parseCloudGrants(connection.cloudGrants)).filter(Boolean).length;
						return (
							<Group
								key={connection.id}
								justify="space-between"
								align="flex-start"
								wrap="nowrap"
								gap="sm"
								data-testid={`external-provider-connection-${connection.id}`}
							>
								<Stack gap={2} style={{ minWidth: 0 }}>
									<Group gap="xs" wrap="wrap">
										<Text fw={600} style={{ overflowWrap: "anywhere" }}>
											{connection.displayName}
										</Text>
										<Badge size="sm" variant="outline" color="gray">
											{t("pages.cloudProviders.kind.external", "OpenAI-compatible")}
										</Badge>
										<Badge size="sm" variant="light" color={locality === "Local" ? "teal" : "orange"}>
											{locality === "Local"
												? t("pages.externalProviders.locality.localBadge", "Declared local")
												: t("pages.externalProviders.locality.cloudBadge", "Declared cloud")}
										</Badge>
										{grantCount > 0 ? (
											<Badge size="sm" variant="light" color="grape" data-testid={`external-provider-grants-${connection.id}`}>
												{t("pages.externalProviders.list.cloudGrantCount", { count: grantCount })}
											</Badge>
										) : null}
										{connection.insecureTransport && !connection.allowInsecureHttp ? (
											<Badge size="sm" variant="light" color="red" data-testid={`external-provider-insecure-${connection.id}`}>
												{t("pages.externalProviders.list.insecureTransport", "Unencrypted HTTP")}
											</Badge>
										) : null}
										{connection.hasApiKey ? (
											<Badge size="sm" variant="light" color="blue">
												{t("pages.externalProviders.list.keyStored", "Key stored")}
											</Badge>
										) : null}
									</Group>
									<Text size="sm" c="dimmed" style={{ wordBreak: "break-all" }}>
										{connection.baseUrl}
									</Text>
									<Text size="xs" c="dimmed">
										{t("pages.externalProviders.list.modelCount", { count: modelCount })}
									</Text>
								</Stack>
								<Button
									variant="light"
									size="xs"
									leftSection={<IconPencil size={14} />}
									data-testid={`external-provider-edit-${connection.id}`}
									onClick={() => onEditExternal(connection.id)}
								>
									{t("pages.externalProviders.list.edit", "Edit")}
								</Button>
							</Group>
						);
					})}
				</Stack>
			)}
		</SectionCard>
	);
}

interface ProviderRowProps {
	readonly name: string;
	readonly detail: string | null;
	readonly status: string;
	readonly testId: string;
	readonly onEdit: () => void;
}

function ProviderRow({ name, detail, status, testId, onEdit }: ProviderRowProps) {
	const { t } = useTranslation();
	return (
		<Group justify="space-between" align="flex-start" wrap="nowrap" gap="sm" data-testid={testId}>
			<Stack gap={2} style={{ minWidth: 0 }}>
				<Text fw={600}>{name}</Text>
				{detail === null ? null : (
					<Text size="sm" c="dimmed" style={{ wordBreak: "break-all" }}>
						{detail}
					</Text>
				)}
				<Text size="xs" c="dimmed">
					{status}
				</Text>
			</Stack>
			<Button variant="light" size="xs" leftSection={<IconPencil size={14} />} data-testid={`${testId}-edit`} onClick={onEdit}>
				{t("pages.externalProviders.list.edit", "Edit")}
			</Button>
		</Group>
	);
}
