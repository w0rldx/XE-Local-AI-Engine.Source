import { Anchor, Badge, Button, Card, Group, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { ExternalAppStatusBadge } from "@/features/externalApps/components/ExternalAppStatusBadge";
import {
	type ExternalAppPermissionName,
	type ExternalAppSummaryView,
	toExternalAppStatus,
} from "@/features/externalApps/models/ExternalAppModels";

interface ExternalAppCatalogCardProps {
	readonly application: ExternalAppSummaryView;
	/** Install is offered only against a ready runtime; the runtime card above carries the explanation. */
	readonly runtimeReady: boolean;
	readonly onInstall: (application: ExternalAppSummaryView) => void;
	readonly onOpenInstance: (instanceId: string) => void;
	readonly "data-testid"?: string;
}

/**
 * One application, and one primary action.
 *
 * Whether it is installed is read from the summary's own `installedInstanceId` / `installedStatus`: the node carries
 * both, so the card never joins against the instances list to find out — a fan-out that would make an N-application
 * catalog issue N+1 reads to render.
 */
export function ExternalAppCatalogCard({
	application,
	runtimeReady,
	onInstall,
	onOpenInstance,
	"data-testid": testId,
}: ExternalAppCatalogCardProps) {
	const { t } = useTranslation();
	const applicationId = application.id ?? "";
	const installedInstanceId = application.installedInstanceId ?? null;

	return (
		<Card withBorder={true} padding="md" radius="md" data-testid={testId ?? `external-app-card-${applicationId}`}>
			<Stack gap="xs" justify="space-between" h="100%">
				<Stack gap="xs">
					<Group justify="space-between" wrap="nowrap" align="flex-start">
						<Text fw={600}>{application.displayName ?? applicationId}</Text>
						{installedInstanceId ? (
							<ExternalAppStatusBadge
								status={toExternalAppStatus(application.installedStatus)}
								data-testid={`external-app-card-status-${applicationId}`}
							/>
						) : null}
					</Group>

					{application.summary ? (
						<Text size="sm" c="dimmed">
							{application.summary}
						</Text>
					) : null}

					<Group gap={4}>
						{grantedPermissionNames(application).map((name) => (
							<Badge key={name} size="sm" variant="light" data-testid={`external-app-card-permission-${name}`}>
								{t(`pages.externalApps.permissions.added.${name}`)}
							</Badge>
						))}
					</Group>

					<Text size="xs" c="dimmed">
						{t("pages.externalApps.catalog.testedVersion", { version: application.testedVersion ?? "" })}
					</Text>
					{application.license ? (
						<Text size="xs" c="dimmed">
							{t("pages.externalApps.catalog.license", { license: application.license })}
						</Text>
					) : null}
					{application.homepage ? (
						<Anchor href={application.homepage} target="_blank" rel="noopener noreferrer" size="xs">
							{t("pages.externalApps.catalog.homepage")}
						</Anchor>
					) : null}
				</Stack>

				<Group justify="flex-end">
					{installedInstanceId ? (
						<Button
							variant="default"
							size="xs"
							onClick={() => onOpenInstance(installedInstanceId)}
							data-testid={`external-app-card-details-${applicationId}`}
						>
							{t("pages.externalApps.catalog.details")}
						</Button>
					) : (
						<Button
							size="xs"
							disabled={!runtimeReady}
							title={runtimeReady ? undefined : t("pages.externalApps.catalog.runtimeNotReady")}
							onClick={() => onInstall(application)}
							data-testid={`external-app-card-install-${applicationId}`}
						>
							{t("pages.externalApps.catalog.install")}
						</Button>
					)}
				</Group>
			</Stack>
		</Card>
	);
}

/**
 * The grants worth a chip on a card: the two network facts plus host files and the graphics card where they are
 * actually granted. The four per-service names are deliberately absent — a card is not the disclosure, the install
 * dialog's permission step is, and a chip has no room to say which part holds the privilege.
 */
function grantedPermissionNames(application: ExternalAppSummaryView): readonly ExternalAppPermissionName[] {
	const permissions = application.permissions;
	const names: ExternalAppPermissionName[] = [];
	if (permissions?.internet === true) {
		names.push("internet");
	}
	if (permissions?.localNetwork === true) {
		names.push("localNetwork");
	}
	if ((permissions?.hostFiles ?? "none") !== "none") {
		names.push("hostFiles");
	}
	if ((permissions?.gpu ?? "none") !== "none") {
		names.push("gpu");
	}
	return names;
}
