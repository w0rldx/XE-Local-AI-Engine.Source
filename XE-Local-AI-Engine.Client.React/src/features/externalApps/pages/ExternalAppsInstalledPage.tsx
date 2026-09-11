import { Anchor, Button, Skeleton, Stack, Table } from "@mantine/core";
import { IconApps } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { ExternalAppStatusBadge } from "@/features/externalApps/components/ExternalAppStatusBadge";
import { InstanceActions } from "@/features/externalApps/components/InstanceActions";
import { toExternalAppStatus } from "@/features/externalApps/models/ExternalAppModels";
import { useExternalAppInstances } from "@/features/externalApps/queries/useExternalApps";

const keyPrefix = "pages.externalApps.installed";

/**
 * Everything XE has installed on this computer.
 *
 * The list returns FULL instance views, so a row's address and actions read straight off it — no per-instance
 * re-read, which is exactly what the widened list exists to avoid. Row actions are the compact subset; the
 * destructive pair lives on the detail page, one click further from a mis-aimed cursor.
 */
export function ExternalAppsInstalledPage() {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const instancesQuery = useExternalAppInstances();
	const instances = instancesQuery.data?.items ?? [];

	const openDetail = (instanceId: string): void => {
		navigate({ to: "/external-apps/instances/$instanceId", params: { instanceId } });
	};

	return (
		<PageShell data-testid="external-apps-installed-page">
			<PageHeader title={t(`${keyPrefix}.title`)} subtitle={t(`${keyPrefix}.subtitle`)} icon={<IconApps size={24} />} />

			{instancesQuery.isLoading ? (
				<Stack gap="sm" data-testid="external-apps-installed-loading">
					<Skeleton height={48} radius="md" />
					<Skeleton height={48} radius="md" />
				</Stack>
			) : instances.length === 0 ? (
				<EmptyState
					message={t(`${keyPrefix}.empty`)}
					action={
						<Anchor
							component="button"
							type="button"
							onClick={() => navigate({ to: "/external-apps/catalog" })}
							data-testid="external-apps-installed-browse"
						>
							{t(`${keyPrefix}.browseCatalog`)}
						</Anchor>
					}
					data-testid="external-apps-installed-empty"
				/>
			) : (
				<Table.ScrollContainer minWidth={720}>
					<Table highlightOnHover={true}>
						<Table.Thead>
							<Table.Tr>
								<Table.Th>{t(`${keyPrefix}.columns.name`)}</Table.Th>
								<Table.Th>{t(`${keyPrefix}.columns.status`)}</Table.Th>
								<Table.Th>{t(`${keyPrefix}.columns.version`)}</Table.Th>
								<Table.Th>{t(`${keyPrefix}.columns.address`)}</Table.Th>
								<Table.Th>{t(`${keyPrefix}.columns.actions`)}</Table.Th>
							</Table.Tr>
						</Table.Thead>
						<Table.Tbody>
							{instances.map((instance) => {
								const instanceId = instance.id ?? "";
								// The single open target: the ONE port the server composed a URL for, never the first one listed.
								const address = (instance.publishedPorts ?? []).find((port) => Boolean(port.url))?.url;
								return (
									<Table.Tr key={instanceId} data-testid={`external-app-row-${instanceId}`}>
										<Table.Td>{instance.displayName ?? instanceId}</Table.Td>
										<Table.Td>
											<ExternalAppStatusBadge
												status={toExternalAppStatus(instance.status)}
												data-testid={`external-app-row-status-${instanceId}`}
											/>
										</Table.Td>
										<Table.Td>{instance.manifestVersion ?? ""}</Table.Td>
										<Table.Td data-testid={`external-app-row-address-${instanceId}`}>
											{address ?? t("pages.externalApps.detail.addressPending")}
										</Table.Td>
										<Table.Td>
											<InstanceActions
												instance={instance}
												compact={true}
												data-testid={`external-app-row-actions-${instanceId}`}
											/>
											<Button
												variant="subtle"
												onClick={() => openDetail(instanceId)}
												data-testid={`external-app-row-details-${instanceId}`}
											>
												{t("pages.externalApps.catalog.details")}
											</Button>
										</Table.Td>
									</Table.Tr>
								);
							})}
						</Table.Tbody>
					</Table>
				</Table.ScrollContainer>
			)}
		</PageShell>
	);
}
