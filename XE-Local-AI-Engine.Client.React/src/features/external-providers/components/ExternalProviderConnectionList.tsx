import { Badge, Button, Group, Stack, Text } from "@mantine/core";
import { IconPencil, IconPlus } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import type { ExternalProviderConnectionDto } from "@/features/external-providers/models/ExternalProviderFormState";
import { parseLocality } from "@/features/external-providers/models/ExternalProviderModel";

interface ExternalProviderConnectionListProps {
	readonly connections: readonly ExternalProviderConnectionDto[];
	readonly disabled: boolean;
	readonly onEdit: (connectionId: string) => void;
	readonly onAdd: () => void;
}

export function ExternalProviderConnectionList({ connections, disabled, onEdit, onAdd }: ExternalProviderConnectionListProps) {
	const { t } = useTranslation();

	return (
		<SectionCard
			title={t("pages.externalProviders.list.title", "Connections")}
			actions={
				<Button
					size="xs"
					leftSection={<IconPlus size={14} />}
					data-testid="external-provider-add-connection"
					disabled={disabled}
					onClick={onAdd}
				>
					{t("pages.externalProviders.list.add", "Add connection")}
				</Button>
			}
			data-testid="external-provider-list"
		>
			{connections.length === 0 ? (
				<EmptyState message={t("pages.externalProviders.list.empty")} data-testid="external-provider-list-empty" />
			) : (
				<Stack gap="sm">
					{connections.map((connection) => {
						const locality = parseLocality(connection.locality);
						const modelCount = connection.models?.length ?? 0;
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
										<Text fw={600}>{connection.displayName}</Text>
										<Badge size="sm" variant="light" color={locality === "Local" ? "teal" : "orange"}>
											{locality === "Local"
												? t("pages.externalProviders.locality.localBadge", "Declared local")
												: t("pages.externalProviders.locality.cloudBadge", "Declared cloud")}
										</Badge>
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
									disabled={disabled}
									onClick={() => onEdit(connection.id)}
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
