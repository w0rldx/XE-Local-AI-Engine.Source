import { Alert, Badge, Button, Card, Checkbox, Group, Select, Stack, Text, TextInput, Title } from "@mantine/core";
import { IconPlayerEject, IconPlayerStop, IconReload, IconTrash } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { toast } from "@/core/ui/notifications/Toast";
import { CudaBuildLogView } from "@/features/node-settings/components/CudaBuildLogView";
import { SourceBuildFormDisclosure } from "@/features/node-settings/components/SourceBuildFormDisclosure";
import { SourceBuildPrerequisiteList } from "@/features/node-settings/components/SourceBuildPrerequisiteList";
import { useImageRuntimeSourceBuildHub } from "@/features/node-settings/hooks/useImageRuntimeSourceBuildHub";
import { useSourceBuildFormDisclosure } from "@/features/node-settings/hooks/useSourceBuildFormDisclosure";
import type {
	ImageRuntimeSourceBackend,
	ImageRuntimeSourceBuildDraft,
} from "@/features/node-settings/models/ImageRuntimeSourceBuildModels";
import { canEjectImageRuntime, idleImageRuntimeActivity } from "@/features/node-settings/models/ImageRuntimeSourceBuildModels";
import {
	mergeSourceBuildLogs,
	sourceBuildIdentity,
	sourceBuildLogEntries,
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
	const [backend, setBackend] = useState<ImageRuntimeSourceBackend>("cpu");
	const [source, setSource] = useState<"official" | "custom">("official");
	const [repository, setRepository] = useState("");
	const [commit, setCommit] = useState("");
	const [acknowledged, setAcknowledged] = useState(false);
	const status = useImageRuntimeSourceBuildStatus();
	const runtime = useImageRuntimeStatus();
	const start = useStartImageRuntimeSourceBuild();
	const cancel = useCancelImageRuntimeSourceBuild();
	const remove = useRemoveImageRuntimeSourceBuild();
	const eject = useEjectImageRuntime();
	const hub = useImageRuntimeSourceBuildHub();

	const managed = runtime.data?.managedRuntime;
	// The installed runtime's identity. `installedAtUtc` moves only when a build actually installs a new runtime, so a
	// refetch of this status (a new object every time, and this query is refetched around every build and eject) leaves
	// it unchanged. Seeding the draft off the object identity instead — which is what an effect keyed on `managed` did —
	// wiped whatever backend, repository or commit the operator had just typed on the next refetch. Adjusted during
	// render rather than in an effect so the seeded values are on the first paint, not one paint later.
	const managedIdentity = managed?.installedAtUtc ?? null;
	const [seededIdentity, setSeededIdentity] = useState<number | null>(null);
	if (managed != null && managedIdentity !== seededIdentity) {
		setSeededIdentity(managedIdentity);
		setBackend(managed.desiredBackend);
		setSource(managed.sourceSelection);
		setRepository(managed.sourceSelection === "custom" ? managed.sourceRepository : "");
		setCommit(
			managed.sourceSelection === "custom" && managed.sourceRevisionMode === "explicitCommit"
				? (managed.sourceRequestedCommit ?? "")
				: "",
		);
		setAcknowledged(false);
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
	// The build form opens itself exactly when it is the answer: a build to watch, a failure to retry, or an invalid
	// record to rebuild. All three come off the status reads, which cost nothing — deciding this by probing would pay
	// the very cost the disclosure exists to defer.
	const buildForm = useSourceBuildFormDisclosure(isBuilding || liveError !== null || managed?.validity === "invalid");
	// The probe runs the toolchain, so it is asked for only while the form that needs its answer is on screen.
	const prerequisites = useImageRuntimeSourceBuildPrerequisites(backend, buildForm.opened);
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
				<Title order={2} size="h4">
					{t("pages.nodeSettings.imageRuntime.sourceBuild.title")}
				</Title>
				<Text size="sm" c="dimmed">
					{t("pages.nodeSettings.imageRuntime.sourceBuild.description")}
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

				{liveError ? <InlineErrorAlert message={liveError} /> : null}
				{isBuilding ? <CudaBuildLogView phase={livePhase} logLines={liveLogEntries.map((entry) => entry.message)} /> : null}

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
							<InlineErrorAlert message={managed.invalidReason} />
						) : null}
					</Stack>
				) : null}

				{isBuilding || activity.residentProcessCount > 0 || managed ? (
					<Group>
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
				) : null}

				<SourceBuildFormDisclosure opened={buildForm.opened} onToggle={buildForm.toggle} testId="image-runtime-source-build-form">
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
							<InlineErrorAlert message={t("pages.nodeSettings.imageRuntime.sourceBuild.riskWarning")} />
							<Checkbox
								checked={acknowledged}
								onChange={(event) => setAcknowledged(event.currentTarget.checked)}
								label={t("pages.nodeSettings.imageRuntime.sourceBuild.riskAcknowledgement")}
							/>
						</Stack>
					) : null}

					<SourceBuildPrerequisiteList
						items={prerequisites.data?.items ?? []}
						translationPrefix="pages.nodeSettings.imageRuntime.sourceBuild"
					/>

					{validationError ? <Alert color="yellow">{validationError}</Alert> : null}

					<Group>
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
					</Group>
				</SourceBuildFormDisclosure>
			</Stack>
		</Card>
	);
}
