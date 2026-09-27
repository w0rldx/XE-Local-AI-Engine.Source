import { Button, SimpleGrid, Skeleton, Stack, Text } from "@mantine/core";
import { IconApps } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { toast } from "@/core/ui/notifications/Toast";
import { ExternalAppCatalogCard } from "@/features/externalApps/components/ExternalAppCatalogCard";
import { InstallDialog } from "@/features/externalApps/components/InstallDialog";
import { RuntimeStatusCard } from "@/features/externalApps/components/RuntimeStatusCard";
import type { ExternalAppSummaryView } from "@/features/externalApps/models/ExternalAppModels";
import {
	useExternalAppCatalog,
	useExternalAppRuntime,
	useRefreshExternalAppCatalog,
} from "@/features/externalApps/queries/useExternalApps";

/**
 * The applications XE can install on this computer.
 *
 * The runtime card sits on top because it is the precondition for everything below it: install is offered only
 * against a ready runtime, and the card is where the operator is told what to do about one that is not.
 */
export function ExternalAppCatalogPage() {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const [installing, setInstalling] = useState<ExternalAppSummaryView | null>(null);

	const runtimeQuery = useExternalAppRuntime();
	const catalogQuery = useExternalAppCatalog();
	const refresh = useRefreshExternalAppCatalog();

	const applications = catalogQuery.data?.applications ?? [];
	const runtimeReady = runtimeQuery.data?.ready === true;
	// `lastRefreshFailure`, not `refreshFailureMessage`: the GET never attempts a refresh, so only the snapshot's
	// most-recent-failure field says the catalog shown is stale. Without this line a refused refresh shows the
	// last-good catalog on a page that looks entirely successful.
	const refreshFailureMessage = catalogQuery.data?.lastRefreshFailure ?? null;

	const openInstance = (instanceId: string): void => {
		navigate({ to: "/external-apps/instances/$instanceId", params: { instanceId } });
	};

	const refreshCatalog = (): void => {
		refresh.mutate(
			{},
			{
				// A refused fetch is still a 200 carrying the last-good catalog; THIS click's failure rides on the body.
				onSuccess: (response) =>
					response.refreshFailureMessage
						? toast.error(t("pages.externalApps.catalog.refreshFailureNotice", { message: response.refreshFailureMessage }))
						: toast.success(t("pages.externalApps.catalog.refreshed")),
				onError: (error) => toast.error(apiErrorMessage(error, t("pages.externalApps.catalog.refreshFailed"))),
			},
		);
	};

	return (
		<PageShell data-testid="external-app-catalog-page">
			<PageHeader
				title={t("pages.externalApps.catalog.title")}
				subtitle={t("pages.externalApps.catalog.subtitle")}
				icon={<IconApps size={24} />}
				actions={
					<Button
						variant="default"
						loading={refresh.isPending}
						onClick={refreshCatalog}
						data-testid="external-app-catalog-refresh"
					>
						{t("pages.externalApps.catalog.refresh")}
					</Button>
				}
			/>

			{refreshFailureMessage ? (
				<Text size="sm" c="dimmed" data-testid="external-app-catalog-refresh-failure">
					{t("pages.externalApps.catalog.refreshFailureNotice", { message: refreshFailureMessage })}
				</Text>
			) : null}

			<RuntimeStatusCard runtime={runtimeQuery.data} isLoading={runtimeQuery.isLoading} />

			{catalogQuery.isLoading ? (
				<Stack gap="sm" data-testid="external-app-catalog-loading">
					<Skeleton height={140} radius="md" />
					<Skeleton height={140} radius="md" />
				</Stack>
			) : applications.length === 0 ? (
				<EmptyState message={t("pages.externalApps.catalog.empty")} data-testid="external-app-catalog-empty" />
			) : (
				<SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }} spacing="md">
					{applications.map((application) => (
						<ExternalAppCatalogCard
							key={application.id ?? ""}
							application={application}
							runtimeReady={runtimeReady}
							onInstall={setInstalling}
							onOpenInstance={openInstance}
						/>
					))}
				</SimpleGrid>
			)}

			{installing ? (
				<InstallDialog application={installing} opened={true} onClose={() => setInstalling(null)} onInstalled={openInstance} />
			) : null}
		</PageShell>
	);
}
