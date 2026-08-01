import { Alert, Anchor, Badge, Button, Card, Group, Loader, Select, Stack, Text, Title } from "@mantine/core";
import {
	IconAlertTriangle,
	IconCloudDownload,
	IconCloudOff,
	IconDownload,
	IconPlayerStop,
	IconRefresh,
	IconRocket,
} from "@tabler/icons-react";
import { Link } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { toast } from "@/core/ui/notifications/Toast";
import {
	type LlamaCppRuntimeStatus,
	type LlamaCppVariant,
	llamaCppVariants,
} from "@/features/node-settings/models/LocalRuntimeModels";
import {
	useEnsureLlamaCppBinary,
	useLlamaCppRuntimeStatus,
	useRefreshLlamaCppRuntime,
	useSourceBuildStatus,
	useUpdateLlamaCppRuntime,
} from "@/features/node-settings/queries/useLocalRuntime";

// Keyed progress-toast id so the update surfaces as ONE in-place animating notification.
const UPDATE_TOAST_ID = "llamacpp-runtime-update";

// Reads the resolved installed/recommended tags off the runtime status. The installed tag is null on a first run that
// has not installed anything yet (the binary resolves from the pin floor); render an em-dash placeholder then.
function installedTag(status: LlamaCppRuntimeStatus | undefined): string {
	return status?.installed?.tag ?? "";
}

function isLlamaCppVariant(value: string): value is LlamaCppVariant {
	return (llamaCppVariants as readonly string[]).includes(value);
}

// The single llama.cpp runtime card. It is the one source of truth for the runtime on the Node Settings page: it shows
// the installed tag + variant (resolved on mount — no operator click), the recommended tag, and (under developer mode)
// the true upstream-latest tag, plus an up-to-date / update-available / offline state. It owns three actions: install
// the recommended (or, under dev mode, the upstream-latest) tag; ensure/select a specific build variant (CPU / Vulkan /
// CUDA); and a manual "Check for updates" that re-resolves recommended + upstream-latest against the GitHub release API.
// Server state is owned by TanStack Query; the only local concerns are the toast lifecycle and the variant-select draft.
// Safety gate (eject-first): while any llama-server child process is running the runtime binary is in use, so the two
// install buttons are disabled and a notice points the operator to the Loaded models page to eject first.
export function LlamaCppUpdaterPanel() {
	const { t } = useTranslation();
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const statusQuery = useLlamaCppRuntimeStatus();
	const updateMutation = useUpdateLlamaCppRuntime();
	const ensureMutation = useEnsureLlamaCppBinary();
	const refreshRuntime = useRefreshLlamaCppRuntime();
	const sourceBuildStatusQuery = useSourceBuildStatus();

	const [isRefreshing, setIsRefreshing] = useState(false);
	// The operator's explicit variant choice, or null until they pick one. The effective Select value derives from this
	// during render (operator choice wins; otherwise the installed variant; otherwise the cpu fallback) — so a node that
	// already runs the Vulkan/CUDA build defaults the "Ensure / select" target to that build, not silently to cpu. Held
	// as a nullable draft (not seeded into state via an effect) to stay controlled without a derived-state render churn.
	const [variantChoice, setVariantChoice] = useState<LlamaCppVariant | null>(null);

	const status = statusQuery.data;
	const isOffline = status?.isOffline === true;
	const updateAvailable = status?.updateAvailable === true;
	const recommendedTag = status?.recommendedTag ?? "";
	const upstreamLatestTag = status?.upstreamLatestTag ?? null;
	const installed = installedTag(status);
	const installedVariant = status?.installed?.variant ?? "";
	const runningProcessCount = status?.runningProcessCount ?? 0;
	const hasRunningProcesses = runningProcessCount > 0;
	const hasInstalledSourceRuntime = status?.installed?.isSourceBuild === true;
	const sourceBuildActive = sourceBuildStatusQuery.data?.isRunning === true;
	const prebuiltMutationBlocked = hasInstalledSourceRuntime || sourceBuildActive;

	// Effective Select value: the operator's explicit pick, else the installed variant (when it is a known build), else
	// the cpu fallback. Recomputed on each render so it tracks the resolved status without copying it into state.
	const selectedVariant: LlamaCppVariant = variantChoice ?? (isLlamaCppVariant(installedVariant) ? installedVariant : "cpu");

	// The default install target is the recommended tag; under developer mode the operator may instead install the
	// true upstream-latest when it differs from recommended. Both go through the same verified update path, and both are
	// blocked while any llama-server process is running (the binary is in use — the operator must eject first).
	const canUpdate =
		updateAvailable &&
		!isOffline &&
		recommendedTag.length > 0 &&
		!updateMutation.isPending &&
		!hasRunningProcesses &&
		!prebuiltMutationBlocked;
	const showUpstream = developerMode && upstreamLatestTag !== null && upstreamLatestTag.length > 0;
	const canInstallUpstream =
		showUpstream &&
		upstreamLatestTag !== installed &&
		!isOffline &&
		!updateMutation.isPending &&
		!hasRunningProcesses &&
		!prebuiltMutationBlocked &&
		upstreamLatestTag !== null;

	const variantData = llamaCppVariants.map((value) => ({
		value,
		label: t(`pages.nodeSettings.llamaCpp.variants.${value}`, value),
	}));

	const handleVariantChange = (value: string | null): void => {
		if (value !== null && isLlamaCppVariant(value)) {
			setVariantChoice(value);
		}
	};

	const handleRefresh = (): void => {
		setIsRefreshing(true);
		refreshRuntime()
			.catch((error: unknown) =>
				toast.error(
					apiErrorMessage(
						error,
						t("pages.nodeSettings.llamaCpp.updater.statusError", "Could not resolve the llama.cpp runtime status."),
					),
				),
			)
			.finally(() => setIsRefreshing(false));
	};

	const handleEnsure = (): void => {
		ensureMutation.mutate(selectedVariant, {
			onSuccess: () => toast.success(t("pages.nodeSettings.llamaCpp.ensured", "llama.cpp binary ready.")),
			onError: (error) =>
				toast.error(apiErrorMessage(error, t("pages.nodeSettings.llamaCpp.ensureError", "Could not ensure the llama.cpp binary."))),
		});
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
					toast.success(t("pages.nodeSettings.llamaCpp.updater.toastSuccess", "llama.cpp runtime updated to {{tag}}.", { tag }), {
						id: UPDATE_TOAST_ID,
						title: t("pages.nodeSettings.llamaCpp.updater.toastSuccessTitle", "Runtime updated"),
					}),
				onError: (error) =>
					toast.error(
						apiErrorMessage(error, t("pages.nodeSettings.llamaCpp.updater.toastError", "Could not update the llama.cpp runtime.")),
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
						<Title order={4}>{t("pages.nodeSettings.llamaCpp.title", "llama.cpp runtime")}</Title>
					</Group>
					<Button
						variant="default"
						leftSection={<IconRefresh size={16} />}
						loading={statusQuery.isFetching || isRefreshing}
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
						{apiErrorMessage(
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
								<Group gap="xs" align="center">
									<Text size="sm" fw={500} ff="monospace" data-testid="llamacpp-updater-installed">
										{installed.length > 0 ? installed : "—"}
									</Text>
									{installedVariant.length > 0 ? (
										<Badge variant="outline" size="sm" data-testid="llamacpp-updater-installed-variant">
											{t(`pages.nodeSettings.llamaCpp.variants.${installedVariant}`, installedVariant)}
										</Badge>
									) : null}
								</Group>
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

						{hasRunningProcesses ? (
							<Alert color="yellow" icon={<IconPlayerStop size={16} />} data-testid="llamacpp-updater-running-notice">
								<Text size="sm">
									{t(
										"pages.nodeSettings.llamaCpp.updater.runningNotice",
										"{{count}} llama.cpp model(s) running — eject them before updating the runtime.",
										{ count: runningProcessCount },
									)}
								</Text>
								<Anchor
									component={Link}
									to={nodeRoutePaths.loadedModels}
									size="sm"
									data-testid="llamacpp-updater-loaded-models-link"
								>
									{t("pages.nodeSettings.llamaCpp.updater.openLoadedModels", "Open Loaded models")}
								</Anchor>
							</Alert>
						) : null}
						{prebuiltMutationBlocked ? (
							<Alert color="yellow" icon={<IconAlertTriangle size={16} />} data-testid="llamacpp-updater-source-build-notice">
								{hasInstalledSourceRuntime
									? t(
											"pages.nodeSettings.llamaCpp.updater.installedSourceBuildNotice",
											"Remove the installed source-built runtime before installing a prebuilt runtime.",
										)
									: t(
											"pages.nodeSettings.llamaCpp.updater.activeSourceBuildNotice",
											"Wait for the active source build to finish or cancel it before installing a prebuilt runtime.",
										)}
							</Alert>
						) : null}

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

						<Group gap="sm" align="flex-end">
							<Select
								label={t("pages.nodeSettings.llamaCpp.selectVariant", "Variant")}
								data={variantData}
								value={selectedVariant}
								onChange={handleVariantChange}
								allowDeselect={false}
								data-testid="llamacpp-updater-variant-select"
							/>
							<Button
								variant="default"
								leftSection={<IconDownload size={16} />}
								loading={ensureMutation.isPending}
								disabled={hasRunningProcesses || prebuiltMutationBlocked}
								onClick={handleEnsure}
								data-testid="llamacpp-updater-ensure-button"
							>
								{t("pages.nodeSettings.llamaCpp.ensure", "Ensure / select")}
							</Button>
						</Group>
					</Stack>
				) : null}
			</Stack>
		</Card>
	);
}
