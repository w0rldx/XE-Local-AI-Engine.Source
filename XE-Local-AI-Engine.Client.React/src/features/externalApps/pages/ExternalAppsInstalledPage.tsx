import { Anchor, Button, Skeleton, Stack } from "@mantine/core";
import { IconApps } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { ResponsiveTable } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
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
				<ResponsiveTable
					rows={instances}
					rowKey={(instance) => instance.id ?? ""}
					rowTestId={(instance) => `external-app-row-${instance.id ?? ""}`}
					minWidth={720}
					striped={false}
					columns={[
						{
							key: "name",
							header: t(`${keyPrefix}.columns.name`),
							primary: true,
							cell: (instance) => instance.displayName ?? instance.id ?? "",
						},
						{
							key: "status",
							header: t(`${keyPrefix}.columns.status`),
							cell: (instance) => (
								<ExternalAppStatusBadge
									status={toExternalAppStatus(instance.status)}
									data-testid={`external-app-row-status-${instance.id ?? ""}`}
								/>
							),
						},
						{ key: "version", header: t(`${keyPrefix}.columns.version`), cell: (instance) => instance.manifestVersion ?? "" },
						{
							key: "address",
							header: t(`${keyPrefix}.columns.address`),
							// The single open target: the ONE port the server composed a URL for, never the first one listed.
							cell: (instance) => (
								<span data-testid={`external-app-row-address-${instance.id ?? ""}`}>
									{(instance.publishedPorts ?? []).find((port) => Boolean(port.url))?.url ??
										t("pages.externalApps.detail.addressPending")}
								</span>
							),
						},
					]}
					actionsHeader={t(`${keyPrefix}.columns.actions`)}
					actions={(instance) => (
						<>
							<InstanceActions instance={instance} compact={true} data-testid={`external-app-row-actions-${instance.id ?? ""}`} />
							<Button
								variant="subtle"
								onClick={() => openDetail(instance.id ?? "")}
								data-testid={`external-app-row-details-${instance.id ?? ""}`}
							>
								{t("pages.externalApps.catalog.details")}
							</Button>
						</>
					)}
				/>
			)}
		</PageShell>
	);
}
