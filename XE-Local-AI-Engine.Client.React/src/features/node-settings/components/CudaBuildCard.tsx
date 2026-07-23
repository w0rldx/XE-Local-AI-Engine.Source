import { Alert, Anchor, Badge, Button, Card, Group, List, Loader, Stack, Text, ThemeIcon, Title } from "@mantine/core";
import {
	IconAlertTriangle,
	IconBolt,
	IconCircleCheck,
	IconCircleX,
	IconCpu,
	IconPlayerStop,
	IconReload,
	IconTrash,
} from "@tabler/icons-react";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { toast } from "@/core/ui/notifications/Toast";
import { CudaBuildLogView } from "@/features/node-settings/components/CudaBuildLogView";
import { useCudaBuildHub } from "@/features/node-settings/hooks/useCudaBuildHub";
import { isLinuxHost } from "@/features/node-settings/models/LocalRuntimeModels";
import {
	useCancelCudaBuild,
	useCudaBuildPrerequisites,
	useCudaBuildStatus,
	useLlamaCppRuntimeStatus,
	useRemoveCudaBuild,
	useStartCudaBuild,
} from "@/features/node-settings/queries/useLocalRuntime";

// The in-app "CUDA (build from source)" card. Double-gated (Locked #9): rendered only under developer mode, and the
// build action is enabled only on a Linux host whose prerequisites are all satisfied (`canBuild`). It owns four states:
// a prerequisite checklist (✓/✗ + detail) with a "Build CUDA" button; a live build view (streamed phase + log via the
// SignalR hub) with Cancel; a managed-CUDA-active view (after a successful build) with Rebuild/Remove and a stale-tag
// "rebuild available" hint; and a disabled state on a non-Linux host that surfaces the unsatisfied reasons. Server state
// is owned by TanStack Query; the live log is UI-only (via the hub). Self-contained like the llama.cpp updater panel.
export function CudaBuildCard() {
	const { t } = useTranslation();
	const developerMode = useDeveloperModeStore((state) => state.developerMode);

	const prereqQuery = useCudaBuildPrerequisites(developerMode);
	const runtimeQuery = useLlamaCppRuntimeStatus(developerMode);
	const cudaStatusQuery = useCudaBuildStatus(developerMode);
	const startMutation = useStartCudaBuild();
	const cancelMutation = useCancelCudaBuild();
	const removeMutation = useRemoveCudaBuild();
	const hub = useCudaBuildHub();

	// Developer-mode opt-in gate (Locked #9): the whole card is hidden when developer mode is off.
	if (!developerMode) {
		return null;
	}

	const prerequisites = prereqQuery.data;
	const runtime = runtimeQuery.data;
	const cudaStatus = cudaStatusQuery.data;

	const isLinux = isLinuxHost(prerequisites);
	const canBuild = prerequisites?.canBuild === true;
	const runningProcessCount = runtime?.runningProcessCount ?? 0;
	const hasRunningProcesses = runningProcessCount > 0;

	// "Building" is derived from the persisted status (survives reconnect) plus the just-fired start. The hub only drives
	// the live phase/log display while in this state.
	const isBuilding = cudaStatus?.isRunning === true || startMutation.isPending;

	// A managed source build is the active runtime (a successful in-app CUDA build was adopted).
	const isManagedCuda = runtime?.isSourceBuild === true;
	const rebuildAvailable = runtime?.rebuildAvailable === true;
	const installedTag = runtime?.installed?.tag ?? "";

	// Live build display: prefer the hub's accumulated deltas; before the first push (e.g. a reload mid-build) fall back
	// to the persisted snapshot from the status query so existing output is not lost.
	const livePhase = hub.phase ?? cudaStatus?.phase ?? null;
	const liveLogLines = hub.logLines.length > 0 ? hub.logLines : (cudaStatus?.logLines ?? []);
	const liveError = hub.error ?? cudaStatus?.sanitizedError ?? null;

	const buildDisabled = !canBuild || hasRunningProcesses || isBuilding;

	const handleBuild = (): void => {
		// Clear any prior run's accumulated live log so a rebuild starts with a clean view.
		hub.reset();
		startMutation.mutate(undefined, {
			onError: (error) =>
				toast.error(
					apiErrorMessage(error, t("pages.nodeSettings.llamaCpp.cudaBuild.startError", "Could not start the CUDA build.")),
				),
		});
	};

	const handleCancel = (): void => {
		cancelMutation.mutate(undefined, {
			onError: (error) =>
				toast.error(
					apiErrorMessage(error, t("pages.nodeSettings.llamaCpp.cudaBuild.cancelError", "Could not cancel the CUDA build.")),
				),
		});
	};

	const handleRemove = (): void => {
		removeMutation.mutate(undefined, {
			onSuccess: () =>
				toast.success(t("pages.nodeSettings.llamaCpp.cudaBuild.removed", "Managed CUDA build removed.")),
			onError: (error) =>
				toast.error(
					apiErrorMessage(error, t("pages.nodeSettings.llamaCpp.cudaBuild.removeError", "Could not remove the CUDA build.")),
				),
		});
	};

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="cuda-build-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Group gap="xs" align="center">
						<IconCpu size={20} />
						<Title order={4}>{t("pages.nodeSettings.llamaCpp.cudaBuild.title", "CUDA (build from source)")}</Title>
						<Badge size="xs" color="grape" variant="light">
							{t("pages.nodeSettings.llamaCpp.cudaBuild.devBadge", "Dev")}
						</Badge>
					</Group>
				</Group>

				<Text size="sm" c="dimmed">
					{t(
						"pages.nodeSettings.llamaCpp.cudaBuild.description",
						"Build a CUDA-accelerated llama.cpp runtime from source on this Linux host. Upstream ships no Linux CUDA prebuilt, so this is the no-build-knowledge path to GPU acceleration.",
					)}
				</Text>

				{prereqQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">
							{t("pages.nodeSettings.llamaCpp.cudaBuild.loading", "Checking build prerequisites…")}
						</Text>
					</Group>
				) : null}

				{prereqQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="cuda-build-prereq-error">
						{apiErrorMessage(
							prereqQuery.error,
							t("pages.nodeSettings.llamaCpp.cudaBuild.prereqError", "Could not check the CUDA build prerequisites."),
						)}
					</Alert>
				) : null}

				{prerequisites && !prereqQuery.isLoading ? (
					<Stack gap="md">
						{!isLinux ? (
							<Alert color="yellow" icon={<IconAlertTriangle size={16} />} data-testid="cuda-build-not-linux">
								{t(
									"pages.nodeSettings.llamaCpp.cudaBuild.notLinux",
									"The in-app CUDA build is available on Linux only. The checklist below shows what this host is missing.",
								)}
							</Alert>
						) : null}

						<List spacing="xs" size="sm" data-testid="cuda-build-checklist">
							{prerequisites.items.map((item) => (
								<List.Item
									key={item.key}
									data-testid={`cuda-build-prereq-${item.key}`}
									icon={
										<ThemeIcon
											color={item.satisfied ? "green" : "red"}
											size={20}
											radius="xl"
											variant="light"
										>
											{item.satisfied ? <IconCircleCheck size={14} /> : <IconCircleX size={14} />}
										</ThemeIcon>
									}
								>
									<Group gap="xs" align="center" wrap="nowrap">
										<Text size="sm" fw={500} ff="monospace">
											{item.key}
										</Text>
										{item.detail.length > 0 ? (
											<Text size="sm" c="dimmed">
												{item.detail}
											</Text>
										) : null}
									</Group>
								</List.Item>
							))}
						</List>

						{isManagedCuda && !isBuilding ? (
							<Stack gap="sm" data-testid="cuda-build-active">
								<Group gap="xs" align="center">
									<Badge color="green" variant="light" data-testid="cuda-build-active-badge">
										{t("pages.nodeSettings.llamaCpp.cudaBuild.activeBadge", "Managed CUDA runtime active")}
									</Badge>
									{installedTag.length > 0 ? (
										<Text size="sm" ff="monospace" data-testid="cuda-build-active-tag">
											{installedTag}
										</Text>
									) : null}
								</Group>

								{rebuildAvailable ? (
									<Alert color="primary" icon={<IconReload size={16} />} data-testid="cuda-build-rebuild-available">
										{t(
											"pages.nodeSettings.llamaCpp.cudaBuild.rebuildAvailable",
											"A newer pinned source tag is available — rebuild to update the managed CUDA runtime.",
										)}
									</Alert>
								) : null}

								{hasRunningProcesses ? (
									<Alert color="yellow" icon={<IconPlayerStop size={16} />} data-testid="cuda-build-active-running-notice">
										<Text size="sm">
											{t(
												"pages.nodeSettings.llamaCpp.cudaBuild.runningNotice",
												"{{count}} llama.cpp model(s) running — eject them before rebuilding or removing the runtime.",
												{ count: runningProcessCount },
											)}
										</Text>
										<Anchor
											component={Link}
											to={nodeRoutePaths.loadedModels}
											size="sm"
											data-testid="cuda-build-loaded-models-link"
										>
											{t("pages.nodeSettings.llamaCpp.cudaBuild.openLoadedModels", "Open Loaded models")}
										</Anchor>
									</Alert>
								) : null}

								<Group gap="sm">
									<Button
										leftSection={<IconReload size={16} />}
										loading={startMutation.isPending}
										disabled={buildDisabled}
										onClick={handleBuild}
										data-testid="cuda-build-rebuild-button"
									>
										{t("pages.nodeSettings.llamaCpp.cudaBuild.rebuild", "Rebuild")}
									</Button>
									<Button
										variant="light"
										color="red"
										leftSection={<IconTrash size={16} />}
										loading={removeMutation.isPending}
										disabled={hasRunningProcesses || isBuilding}
										onClick={handleRemove}
										data-testid="cuda-build-remove-button"
									>
										{t("pages.nodeSettings.llamaCpp.cudaBuild.remove", "Remove")}
									</Button>
								</Group>
							</Stack>
						) : null}

						{isBuilding ? (
							<Stack gap="sm" data-testid="cuda-build-progress">
								<CudaBuildLogView phase={livePhase} logLines={liveLogLines} />
								{liveError !== null && liveError.length > 0 ? (
									<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="cuda-build-error">
										{liveError}
									</Alert>
								) : null}
								<Group gap="sm">
									<Button
										variant="light"
										color="red"
										leftSection={<IconPlayerStop size={16} />}
										loading={cancelMutation.isPending}
										onClick={handleCancel}
										data-testid="cuda-build-cancel-button"
									>
										{t("pages.nodeSettings.llamaCpp.cudaBuild.cancel", "Cancel build")}
									</Button>
								</Group>
							</Stack>
						) : null}

						{!isManagedCuda && !isBuilding ? (
							<Stack gap="sm">
								{hasRunningProcesses ? (
									<Alert color="yellow" icon={<IconPlayerStop size={16} />} data-testid="cuda-build-running-notice">
										<Text size="sm">
											{t(
												"pages.nodeSettings.llamaCpp.cudaBuild.runningNotice",
												"{{count}} llama.cpp model(s) running — eject them before rebuilding or removing the runtime.",
												{ count: runningProcessCount },
											)}
										</Text>
										<Anchor
											component={Link}
											to={nodeRoutePaths.loadedModels}
											size="sm"
											data-testid="cuda-build-running-loaded-models-link"
										>
											{t("pages.nodeSettings.llamaCpp.cudaBuild.openLoadedModels", "Open Loaded models")}
										</Anchor>
									</Alert>
								) : null}
								<Group gap="sm">
									<Button
										leftSection={<IconBolt size={16} />}
										loading={startMutation.isPending}
										disabled={buildDisabled}
										onClick={handleBuild}
										data-testid="cuda-build-start-button"
									>
										{t("pages.nodeSettings.llamaCpp.cudaBuild.build", "Build CUDA")}
									</Button>
								</Group>
							</Stack>
						) : null}
					</Stack>
				) : null}
			</Stack>
		</Card>
	);
}
