import { Alert, Badge, Button, Card, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconCloudDownload, IconCloudOff, IconRefresh, IconRocket } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { toast } from "@/core/ui/notifications/Toast";
import type { LlamaCppRuntimeStatus } from "@/features/node-settings/models/LocalRuntimeModels";
import { useLlamaCppRuntimeStatus, useUpdateLlamaCppRuntime } from "@/features/node-settings/queries/useLocalRuntime";

// Keyed progress-toast id so the update surfaces as ONE in-place animating notification (mirrors useModelPull).
const UPDATE_TOAST_ID = "llamacpp-runtime-update";

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

// Reads the resolved installed/recommended tags off the runtime status. The installed tag is null on a first run that
// has not installed anything yet (the binary resolves from the pin floor); render an em-dash placeholder then.
function installedTag(status: LlamaCppRuntimeStatus | undefined): string {
	return status?.installed?.tag ?? "";
}

// llama.cpp runtime updater: shows the installed tag vs the recommended tag (and, under developer mode, the true
// upstream-latest tag), an up-to-date / update-available state, and an Install/Update button that targets the chosen
// tag. Server state (status + update) is owned by TanStack Query; the only local concern is the toast lifecycle, which
// is driven imperatively from the mutation callbacks. Offline disables the update button (the recommended tag is still
// served from cache/pins, but a verified download needs the GitHub release API).
export function LlamaCppUpdaterPanel() {
	const { t } = useTranslation();
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const statusQuery = useLlamaCppRuntimeStatus();
	const updateMutation = useUpdateLlamaCppRuntime();

	const status = statusQuery.data;
	const isOffline = status?.isOffline === true;
	const updateAvailable = status?.updateAvailable === true;
	const recommendedTag = status?.recommendedTag ?? "";
	const upstreamLatestTag = status?.upstreamLatestTag ?? null;
	const installed = installedTag(status);

	// The default install target is the recommended tag; under developer mode the operator may instead install the
	// true upstream-latest when it differs from recommended. Both go through the same verified update path.
	const canUpdate = updateAvailable && !isOffline && recommendedTag.length > 0 && !updateMutation.isPending;
	const showUpstream = developerMode && upstreamLatestTag !== null && upstreamLatestTag.length > 0;
	const canInstallUpstream =
		showUpstream && upstreamLatestTag !== installed && !isOffline && !updateMutation.isPending && upstreamLatestTag !== null;

	const handleRefresh = (): void => {
		statusQuery.refetch().catch(() => undefined);
	};

	const runUpdate = (tag: string): void => {
		if (tag.length === 0) {
			return;
		}
		toast.progress({
			id: UPDATE_TOAST_ID,
			title: t("pages.nodeSettings.llamaCpp.updater.toastTitle", "Updating llama.cpp runtime"),
			message: t("pages.nodeSettings.llamaCpp.updater.toastPreparing", "Downloading and verifying {{tag}}…", { tag }),
		});
		updateMutation.mutate(
			{ tag },
			{
				onSuccess: () =>
					toast.success(
						t("pages.nodeSettings.llamaCpp.updater.toastSuccess", "llama.cpp runtime updated to {{tag}}.", { tag }),
						{ id: UPDATE_TOAST_ID, title: t("pages.nodeSettings.llamaCpp.updater.toastSuccessTitle", "Runtime updated") },
					),
				onError: (error) =>
					toast.error(
						errorMessage(error, t("pages.nodeSettings.llamaCpp.updater.toastError", "Could not update the llama.cpp runtime.")),
						{ id: UPDATE_TOAST_ID, title: t("pages.nodeSettings.llamaCpp.updater.toastErrorTitle", "Update failed") },
					),
			},
		);
	};

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="llamacpp-updater-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Group gap="xs" align="center">
						<IconRocket size={20} />
						<Title order={4}>{t("pages.nodeSettings.llamaCpp.updater.title", "llama.cpp runtime updates")}</Title>
					</Group>
					<Button
						variant="default"
						leftSection={<IconRefresh size={16} />}
						loading={statusQuery.isFetching}
						onClick={handleRefresh}
						data-testid="llamacpp-updater-refresh-button"
					>
						{t("pages.nodeSettings.llamaCpp.updater.refresh", "Check for updates")}
					</Button>
				</Group>

				{statusQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.nodeSettings.llamaCpp.updater.loading", "Resolving runtime status…")}</Text>
					</Group>
				) : null}

				{statusQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="llamacpp-updater-error">
						{errorMessage(
							statusQuery.error,
							t("pages.nodeSettings.llamaCpp.updater.statusError", "Could not resolve the llama.cpp runtime status."),
						)}
					</Alert>
				) : null}

				{status && !statusQuery.isLoading ? (
					<Stack gap="md">
						<Group gap="xl" align="flex-start">
							<Stack gap={2}>
								<Text size="xs" c="dimmed">
									{t("pages.nodeSettings.llamaCpp.updater.installed", "Installed")}
								</Text>
								<Text size="sm" fw={500} ff="monospace" data-testid="llamacpp-updater-installed">
									{installed.length > 0 ? installed : "—"}
								</Text>
							</Stack>
							<Stack gap={2}>
								<Text size="xs" c="dimmed">
									{t("pages.nodeSettings.llamaCpp.updater.recommended", "Recommended")}
								</Text>
								<Text size="sm" fw={500} ff="monospace" data-testid="llamacpp-updater-recommended">
									{recommendedTag.length > 0 ? recommendedTag : "—"}
								</Text>
							</Stack>
							{showUpstream ? (
								<Stack gap={2}>
									<Group gap={4} align="center">
										<Text size="xs" c="dimmed">
											{t("pages.nodeSettings.llamaCpp.updater.upstream", "Upstream latest")}
										</Text>
										<Badge size="xs" color="grape" variant="light">
											{t("pages.nodeSettings.llamaCpp.updater.devBadge", "Dev")}
										</Badge>
									</Group>
									<Text size="sm" fw={500} ff="monospace" data-testid="llamacpp-updater-upstream">
										{upstreamLatestTag}
									</Text>
								</Stack>
							) : null}
						</Group>

						{isOffline ? (
							<Alert color="yellow" icon={<IconCloudOff size={16} />} data-testid="llamacpp-updater-offline">
								{t(
									"pages.nodeSettings.llamaCpp.updater.offline",
									"Offline — using the cached / pinned runtime. Update checks are unavailable until the GitHub release API is reachable.",
								)}
							</Alert>
						) : updateAvailable ? (
							<Badge color="primary" variant="light" data-testid="llamacpp-updater-state-available">
								{t("pages.nodeSettings.llamaCpp.updater.updateAvailable", "Update available")}
							</Badge>
						) : (
							<Badge color="green" variant="light" data-testid="llamacpp-updater-state-uptodate">
								{t("pages.nodeSettings.llamaCpp.updater.upToDate", "Up to date")}
							</Badge>
						)}

						<Group gap="sm">
							<Button
								leftSection={<IconCloudDownload size={16} />}
								loading={updateMutation.isPending}
								disabled={!canUpdate}
								onClick={() => runUpdate(recommendedTag)}
								data-testid="llamacpp-updater-update-button"
							>
								{t("pages.nodeSettings.llamaCpp.updater.update", "Install recommended ({{tag}})", {
									tag: recommendedTag.length > 0 ? recommendedTag : "—",
								})}
							</Button>
							{showUpstream && upstreamLatestTag !== null ? (
								<Button
									variant="light"
									color="grape"
									leftSection={<IconCloudDownload size={16} />}
									loading={updateMutation.isPending}
									disabled={!canInstallUpstream}
									onClick={() => runUpdate(upstreamLatestTag)}
									data-testid="llamacpp-updater-upstream-button"
								>
									{t("pages.nodeSettings.llamaCpp.updater.installUpstream", "Install upstream ({{tag}})", {
										tag: upstreamLatestTag,
									})}
								</Button>
							) : null}
						</Group>
					</Stack>
				) : null}
			</Stack>
		</Card>
	);
}
