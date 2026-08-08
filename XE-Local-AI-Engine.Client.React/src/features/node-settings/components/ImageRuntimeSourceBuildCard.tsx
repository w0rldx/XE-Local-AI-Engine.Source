import {
	Alert,
	Badge,
	Button,
	Card,
	Checkbox,
	Group,
	List,
	Select,
	Stack,
	Text,
	TextInput,
	ThemeIcon,
	Title,
} from "@mantine/core";
import {
	IconAlertTriangle,
	IconCircleCheck,
	IconCircleX,
	IconPlayerEject,
	IconPlayerStop,
	IconReload,
	IconTrash,
} from "@tabler/icons-react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { toast } from "@/core/ui/notifications/Toast";
import { CudaBuildLogView } from "@/features/node-settings/components/CudaBuildLogView";
import { useImageRuntimeSourceBuildHub } from "@/features/node-settings/hooks/useImageRuntimeSourceBuildHub";
import type {
	ImageRuntimeSourceBackend,
	ImageRuntimeSourceBuildDraft,
} from "@/features/node-settings/models/ImageRuntimeSourceBuildModels";
import { canEjectImageRuntime, idleImageRuntimeActivity } from "@/features/node-settings/models/ImageRuntimeSourceBuildModels";
import {
	mergeSourceBuildLogs,
	sourceBuildIdentity,
	sourceBuildLogEntries,
	sourceBuildPrerequisiteDiagnostic,
	sourceBuildValidationIssue,
} from "@/features/node-settings/models/SourceBuildModels";
import {
	useCancelImageRuntimeSourceBuild,
	useEjectImageRuntime,
	useImageRuntimeSourceBuildPrerequisites,
	useImageRuntimeSourceBuildStatus,
	useImageRuntimeStatus,
	useRemoveImageRuntimeSourceBuild,
	useStartImageRuntimeSourceBuild,
} from "@/features/node-settings/queries/useImageRuntime";

const officialRepository = "https://github.com/leejet/stable-diffusion.cpp";

export function ImageRuntimeSourceBuildCard() {
	const { t } = useTranslation();
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const [backend, setBackend] = useState<ImageRuntimeSourceBackend>("cpu");
	const [source, setSource] = useState<"official" | "custom">("official");
	const [repository, setRepository] = useState("");
	const [commit, setCommit] = useState("");
	const [acknowledged, setAcknowledged] = useState(false);
	const prerequisites = useImageRuntimeSourceBuildPrerequisites(backend, developerMode);
	const status = useImageRuntimeSourceBuildStatus(developerMode);
	// Runtime status stays enabled outside Developer Mode so an invalid fail-closed tombstone can always expose its
	// eject/remove recovery path. The source-build probes, status stream, and build controls remain developer-only.
	const runtime = useImageRuntimeStatus(true);
	const start = useStartImageRuntimeSourceBuild();
	const cancel = useCancelImageRuntimeSourceBuild();
	const remove = useRemoveImageRuntimeSourceBuild();
	const eject = useEjectImageRuntime();
	const hub = useImageRuntimeSourceBuildHub(developerMode);

	const managed = runtime.data?.managedRuntime;
	useEffect(() => {
		if (managed == null) {
			return;
		}
		setBackend(managed.desiredBackend);
		setSource(managed.sourceSelection);
		setRepository(managed.sourceSelection === "custom" ? managed.sourceRepository : "");
		setCommit(
			managed.sourceSelection === "custom" && managed.sourceRevisionMode === "explicitCommit"
				? (managed.sourceRequestedCommit ?? "")
				: "",
		);
		setAcknowledged(false);
	}, [managed]);

	const recoveryOnly = !developerMode && managed?.validity === "invalid";
	if (!developerMode && !recoveryOnly) {
		return null;
	}

	const draft: ImageRuntimeSourceBuildDraft = {
		backend,
		source,
		repository,
		commit,
		acknowledgeCustomSourceRisk: acknowledged,
	};
	const validationIssue = sourceBuildValidationIssue(draft);
	const validationError =
		validationIssue === null ? null : t(`pages.nodeSettings.imageRuntime.sourceBuild.validation.${validationIssue}`);
	const buildStatus = status.data;
	const isBuilding = buildStatus?.isRunning === true || start.isPending;
	const activity = runtime.data?.activity ?? idleImageRuntimeActivity;
	const current = buildStatus?.currentBuild;
	const livePhase = hub.phase ?? buildStatus?.phase ?? null;
	const persistedIdentity = sourceBuildIdentity(current);
	const liveLogEntries =
		hub.buildIdentity === null || hub.buildIdentity === persistedIdentity
			? mergeSourceBuildLogs(
					sourceBuildLogEntries(buildStatus?.logStartSequence ?? 0, buildStatus?.logLines ?? []),
					hub.logEntries,
				)
			: hub.logEntries;
	const liveError = hub.error ?? buildStatus?.sanitizedError ?? null;
	const revisionKey = source === "official" ? "enginePinned" : commit.trim().length > 0 ? "explicitCommit" : "defaultBranch";
	const backendOptions = (["cpu", "vulkan", "cuda"] as const).map((value) => ({
		value,
		label: t(`pages.nodeSettings.imageRuntime.sourceBuild.backends.${value}`),
	}));
	const sourceOptions = (["official", "custom"] as const).map((value) => ({
		value,
		label: t(`pages.nodeSettings.imageRuntime.sourceBuild.sources.${value}`),
	}));

	const showError = (error: unknown, key: string): void => {
		toast.error(apiErrorMessage(error, t(key)));
	};
	const run = (): void => {
		hub.reset();
		start.mutate(draft, {
			onError: (error) => showError(error, "pages.nodeSettings.imageRuntime.sourceBuild.startError"),
		});
		if (source === "custom") {
			setAcknowledged(false);
		}
	};

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="image-runtime-source-build-card">
			<Stack gap="md">
				<Group gap="xs">
					<Title order={4}>{t("pages.nodeSettings.imageRuntime.sourceBuild.title")}</Title>
					<Badge color={recoveryOnly ? "red" : "grape"}>
						{t(
							recoveryOnly
								? "pages.nodeSettings.imageRuntime.sourceBuild.recoveryBadge"
								: "pages.nodeSettings.imageRuntime.sourceBuild.devBadge",
						)}
					</Badge>
				</Group>
				<Text size="sm" c="dimmed">
					{t(
						recoveryOnly
							? "pages.nodeSettings.imageRuntime.sourceBuild.recoveryDescription"
							: "pages.nodeSettings.imageRuntime.sourceBuild.description",
					)}
				</Text>

				<Group gap="xs">
					<Badge color={activity.isBusy ? "yellow" : "gray"}>
						{t(`pages.nodeSettings.imageRuntime.sourceBuild.activity.${activity.isBusy ? "busy" : "idle"}`)}
					</Badge>
					{activity.isBusy ? (
						<Text size="sm" c="dimmed" data-testid="image-runtime-activity">
							{t("pages.nodeSettings.imageRuntime.sourceBuild.activity.detail", {
								jobs: activity.activeJobCount,
								starting: activity.spawnReadinessCount,
								processes: activity.residentProcessCount,
							})}
						</Text>
					) : null}
				</Group>

				{developerMode ? (
					<>
						<Group grow={true} align="start">
							<Select
								label={t("pages.nodeSettings.imageRuntime.sourceBuild.backend")}
								value={backend}
								data={backendOptions}
								onChange={(value) => value && setBackend(value as ImageRuntimeSourceBackend)}
							/>
							<Select
								label={t("pages.nodeSettings.imageRuntime.sourceBuild.source")}
								value={source}
								data={sourceOptions}
								onChange={(value) => {
									if (value === "official" || value === "custom") {
										setSource(value);
										setAcknowledged(false);
										if (value === "official") {
											setCommit("");
										}
									}
								}}
							/>
						</Group>

						<Text size="sm" c="dimmed" data-testid="image-runtime-revision-behavior">
							{t(`pages.nodeSettings.imageRuntime.sourceBuild.revisionBehavior.${revisionKey}`)}
						</Text>

						{source === "custom" ? (
							<Stack gap="sm">
								<TextInput
									label={t("pages.nodeSettings.imageRuntime.sourceBuild.repository")}
									placeholder={t("pages.nodeSettings.imageRuntime.sourceBuild.repositoryPlaceholder")}
									value={repository}
									onChange={(event) => {
										setRepository(event.currentTarget.value);
										setAcknowledged(false);
									}}
								/>
								<TextInput
									label={t("pages.nodeSettings.imageRuntime.sourceBuild.commit")}
									value={commit}
									onChange={(event) => setCommit(event.currentTarget.value)}
								/>
								<Alert color="red" icon={<IconAlertTriangle size={16} />}>
									{t("pages.nodeSettings.imageRuntime.sourceBuild.riskWarning")}
								</Alert>
								<Checkbox
									checked={acknowledged}
									onChange={(event) => setAcknowledged(event.currentTarget.checked)}
									label={t("pages.nodeSettings.imageRuntime.sourceBuild.riskAcknowledgement")}
								/>
							</Stack>
						) : null}

						<List spacing="xs" size="sm">
							{(prerequisites.data?.items ?? []).map((item) => {
								const diagnostic = sourceBuildPrerequisiteDiagnostic(item);
								return (
									<List.Item
										key={item.key}
										icon={
											<ThemeIcon color={item.satisfied ? "green" : "red"} size={20} radius="xl" variant="light">
												{item.satisfied ? <IconCircleCheck size={14} /> : <IconCircleX size={14} />}
											</ThemeIcon>
										}
									>
										<Text span={true} fw={500}>
											{t(`pages.nodeSettings.imageRuntime.sourceBuild.prerequisites.${item.key}`, item.key)}
										</Text>{" "}
										<Text span={true} c="dimmed">
											{t(
												`pages.nodeSettings.imageRuntime.sourceBuild.prerequisiteAvailability.${item.satisfied ? "available" : "missing"}`,
											)}
											{diagnostic ? ` · ${diagnostic}` : ""}
										</Text>
									</List.Item>
								);
							})}
						</List>

						{validationError ? <Alert color="yellow">{validationError}</Alert> : null}
						{liveError ? <Alert color="red">{liveError}</Alert> : null}
						{isBuilding ? <CudaBuildLogView phase={livePhase} logLines={liveLogEntries.map((entry) => entry.message)} /> : null}
					</>
				) : null}

				{managed ? (
					<Stack gap="xs" data-testid="managed-image-runtime-status">
						<Group>
							<Badge color={managed.validity === "active" ? "green" : "red"}>
								{t(`pages.nodeSettings.imageRuntime.sourceBuild.validity.${managed.validity}`, {
									backend: managed.desiredBackend,
								})}
							</Badge>
							{managed.sourceCommit ? <Text ff="monospace">{managed.sourceCommit.slice(0, 12)}</Text> : null}
						</Group>
						<Text size="sm" c="dimmed">
							{managed.sourceRepository || officialRepository} ·{" "}
							{t(`pages.nodeSettings.imageRuntime.sourceBuild.sources.${managed.sourceSelection}`)} ·{" "}
							{t(`pages.nodeSettings.imageRuntime.sourceBuild.revisions.${managed.sourceRevisionMode}`)}
						</Text>
						{managed.validity === "invalid" && managed.invalidReason ? (
							<Alert color="red" icon={<IconAlertTriangle size={16} />}>
								{managed.invalidReason}
							</Alert>
						) : null}
					</Stack>
				) : null}

				<Group>
					{developerMode ? (
						<>
							<Button
								leftSection={<IconReload size={16} />}
								onClick={run}
								loading={start.isPending}
								disabled={validationError !== null || prerequisites.data?.canBuild !== true || isBuilding || activity.isBusy}
							>
								{managed
									? t("pages.nodeSettings.imageRuntime.sourceBuild.rebuild")
									: t("pages.nodeSettings.imageRuntime.sourceBuild.build")}
							</Button>
							{isBuilding ? (
								<Button
									color="yellow"
									leftSection={<IconPlayerStop size={16} />}
									onClick={() =>
										cancel.mutate(undefined, {
											onError: (error) => showError(error, "pages.nodeSettings.imageRuntime.sourceBuild.cancelError"),
										})
									}
									loading={cancel.isPending}
								>
									{t("pages.nodeSettings.imageRuntime.sourceBuild.cancel")}
								</Button>
							) : null}
						</>
					) : null}
					{activity.residentProcessCount > 0 ? (
						<Button
							variant="light"
							leftSection={<IconPlayerEject size={16} />}
							disabled={!canEjectImageRuntime(activity)}
							loading={eject.isPending}
							onClick={() =>
								eject.mutate(undefined, {
									onError: (error) => showError(error, "pages.nodeSettings.imageRuntime.sourceBuild.ejectError"),
								})
							}
						>
							{t("pages.nodeSettings.imageRuntime.sourceBuild.eject")}
						</Button>
					) : null}
					{managed ? (
						<Button
							color="red"
							variant="light"
							leftSection={<IconTrash size={16} />}
							disabled={isBuilding || activity.isBusy}
							loading={remove.isPending}
							onClick={() =>
								remove.mutate(undefined, {
									onError: (error) => showError(error, "pages.nodeSettings.imageRuntime.sourceBuild.removeError"),
								})
							}
						>
							{t("pages.nodeSettings.imageRuntime.sourceBuild.remove")}
						</Button>
					) : null}
				</Group>
			</Stack>
		</Card>
	);
}
