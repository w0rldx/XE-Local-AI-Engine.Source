import { Alert, Badge, Button, Card, Checkbox, Group, Select, Stack, Text, TextInput, Title } from "@mantine/core";
import { IconCircleCheck, IconPlayerEject, IconPlayerStop, IconReload, IconTrash } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { toast } from "@/core/ui/notifications/Toast";
import { CudaBuildLogView } from "@/features/node-settings/components/CudaBuildLogView";
import { SourceBuildFormDisclosure } from "@/features/node-settings/components/SourceBuildFormDisclosure";
import { SourceBuildPrerequisiteList } from "@/features/node-settings/components/SourceBuildPrerequisiteList";
import { useSourceBuildFormDisclosure } from "@/features/node-settings/hooks/useSourceBuildFormDisclosure";
import { sourceBuildLogEntries, sourceBuildValidationIssue } from "@/features/node-settings/models/SourceBuildModels";
import type {
	WhisperSourceBackend,
	WhisperSourceBuildDraft,
} from "@/features/node-settings/models/WhisperRuntimeSourceBuildModels";
import {
	canEjectWhisperRuntime,
	idleWhisperRuntimeActivity,
} from "@/features/node-settings/models/WhisperRuntimeSourceBuildModels";
import {
	useCancelWhisperSourceBuild,
	useEjectWhisperRuntime,
	useRemoveWhisperSourceBuild,
	useStartWhisperSourceBuild,
	useWhisperRuntimeStatus,
	useWhisperSourceBuildPrerequisites,
	useWhisperSourceBuildStatus,
} from "@/features/node-settings/queries/useWhisperRuntime";

const officialRepository = "https://github.com/ggml-org/whisper.cpp";
const translationPrefix = "pages.nodeSettings.transcriptionRuntime.sourceBuild";

/**
 * The managed whisper.cpp build, and the only way a Linux + NVIDIA node gets a GPU transcription runtime: upstream
 * publishes no Linux CUDA asset, so that box otherwise resolves the CPU tarball for good.
 *
 * Structurally the third instance of the llama.cpp / stable-diffusion.cpp source-build card, with one real
 * difference: whisper.cpp registers no SignalR hub, so the status query's own poll is the live phase and log rather
 * than a hub stream merged over a persisted tail.
 */
export function WhisperRuntimeSourceBuildCard() {
	const { t } = useTranslation();
	const [backend, setBackend] = useState<WhisperSourceBackend>("cuda");
	const [source, setSource] = useState<"official" | "custom">("official");
	const [repository, setRepository] = useState("");
	const [commit, setCommit] = useState("");
	const [acknowledged, setAcknowledged] = useState(false);
	const [removeConfirmOpen, setRemoveConfirmOpen] = useState(false);
	const status = useWhisperSourceBuildStatus();
	const runtime = useWhisperRuntimeStatus();
	const start = useStartWhisperSourceBuild();
	const cancel = useCancelWhisperSourceBuild();
	const remove = useRemoveWhisperSourceBuild();
	const eject = useEjectWhisperRuntime();

	const managed = runtime.data?.managedRuntime;
	// `installedAtUtc` moves only when a build actually installs a runtime, so seeding off it — rather than off the
	// `managed` object, which is a new reference on every refetch — leaves a half-typed draft alone. Adjusted during
	// render rather than in an effect so the seeded values are on the first paint.
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

	const draft: WhisperSourceBuildDraft = {
		backend,
		source,
		repository,
		commit,
		acknowledgeCustomSourceRisk: acknowledged,
	};
	const validationIssue = sourceBuildValidationIssue(draft);
	const validationError = validationIssue === null ? null : t(`${translationPrefix}.validation.${validationIssue}`);
	const buildStatus = status.data;
	const isBuilding = buildStatus?.isRunning === true || start.isPending;
	const activity = runtime.data?.activity ?? idleWhisperRuntimeActivity;
	// A build the node interrupted (a restart mid-build) comes back as a terminal `failed` phase carrying the
	// recovery sentence, so the same alert covers a genuine failure and a recovered one — the server's words either
	// way, never the SPA's guess at which happened.
	const liveError = buildStatus?.sanitizedError ?? null;
	// The build form opens itself exactly when it is the answer: a build to watch, a failure to retry, or an invalid
	// record to rebuild. All three come off the status reads, which cost nothing — deciding this by probing would pay
	// the very cost the disclosure exists to defer.
	const buildForm = useSourceBuildFormDisclosure(isBuilding || liveError !== null || managed?.validity === "invalid");
	// The probe runs the toolchain, so it is asked for only while the form that needs its answer is on screen.
	const prerequisites = useWhisperSourceBuildPrerequisites(backend, buildForm.opened);
	const logLines = sourceBuildLogEntries(buildStatus?.logStartSequence ?? 0, buildStatus?.logLines ?? []).map(
		(entry) => entry.message,
	);
	// `completed` is also the phase a REMOVE settles on, so the adopted-runtime record is what tells a finished build
	// apart from a finished removal; without it the card would congratulate the operator on adopting what they just
	// deleted.
	const succeeded =
		managed != null && buildStatus?.terminal === true && buildStatus.phase === "completed" && buildStatus.sanitizedError === null;
	const revisionKey = source === "official" ? "enginePinned" : commit.trim().length > 0 ? "explicitCommit" : "defaultBranch";
	const backendOptions = (["cuda", "cpu"] as const).map((value) => ({
		value,
		label: t(`${translationPrefix}.backends.${value}`),
	}));
	const sourceOptions = (["official", "custom"] as const).map((value) => ({
		value,
		label: t(`${translationPrefix}.sources.${value}`),
	}));

	const showError = (error: unknown, key: string): void => {
		toast.error(apiErrorMessage(error, t(key)));
	};
	const run = (): void => {
		start.mutate(draft, {
			onError: (error) => showError(error, `${translationPrefix}.startError`),
		});
		if (source === "custom") {
			setAcknowledged(false);
		}
	};
	const confirmRemove = (): void => {
		setRemoveConfirmOpen(false);
		remove.mutate(undefined, {
			onError: (error) => showError(error, `${translationPrefix}.removeError`),
		});
	};

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="whisper-runtime-source-build-card">
			<Stack gap="md">
				<Title order={2} size="h4">
					{t(`${translationPrefix}.title`)}
				</Title>
				<Text size="sm" c="dimmed">
					{t(`${translationPrefix}.description`)}
				</Text>

				<Group gap="xs">
					<Badge color={activity.isBusy ? "yellow" : "gray"}>
						{t(`${translationPrefix}.activity.${activity.isBusy ? "busy" : "idle"}`)}
					</Badge>
					{activity.isBusy ? (
						<Text size="sm" c="dimmed" data-testid="whisper-runtime-activity">
							{t(`${translationPrefix}.activity.detail`, {
								transcriptions: activity.activeTranscriptionCount,
								starting: activity.spawnReadinessCount,
								processes: activity.residentProcessCount,
							})}
						</Text>
					) : null}
				</Group>

				{liveError ? <InlineErrorAlert message={liveError} /> : null}
				{succeeded ? (
					<Alert color="green" icon={<IconCircleCheck size={16} />} data-testid="whisper-source-build-succeeded">
						{t(`${translationPrefix}.succeeded`)}
					</Alert>
				) : null}
				{isBuilding ? <CudaBuildLogView phase={buildStatus?.phase ?? null} logLines={logLines} /> : null}

				{managed ? (
					<Stack gap="xs" data-testid="managed-whisper-runtime-status">
						<Group>
							<Badge color={managed.validity === "active" ? "green" : "red"}>
								{t(`${translationPrefix}.validity.${managed.validity}`, { backend: managed.desiredBackend })}
							</Badge>
							{managed.sourceCommit ? <Text ff="monospace">{managed.sourceCommit.slice(0, 12)}</Text> : null}
						</Group>
						<Text size="sm" c="dimmed">
							{managed.sourceRepository || officialRepository} · {t(`${translationPrefix}.sources.${managed.sourceSelection}`)} ·{" "}
							{t(`${translationPrefix}.revisions.${managed.sourceRevisionMode}`)}
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
										onError: (error) => showError(error, `${translationPrefix}.cancelError`),
									})
								}
								loading={cancel.isPending}
							>
								{t(`${translationPrefix}.cancel`)}
							</Button>
						) : null}
						{activity.residentProcessCount > 0 ? (
							<Button
								variant="light"
								leftSection={<IconPlayerEject size={16} />}
								disabled={!canEjectWhisperRuntime(activity)}
								loading={eject.isPending}
								onClick={() =>
									eject.mutate(undefined, {
										onError: (error) => showError(error, `${translationPrefix}.ejectError`),
									})
								}
							>
								{t(`${translationPrefix}.eject`)}
							</Button>
						) : null}
						{managed ? (
							<Button
								color="red"
								variant="light"
								leftSection={<IconTrash size={16} />}
								disabled={isBuilding || activity.isBusy}
								loading={remove.isPending}
								onClick={() => setRemoveConfirmOpen(true)}
							>
								{t(`${translationPrefix}.remove`)}
							</Button>
						) : null}
					</Group>
				) : null}

				<SourceBuildFormDisclosure opened={buildForm.opened} onToggle={buildForm.toggle} testId="whisper-source-build-form">
					<Group grow={true} align="start">
						<Select
							label={t(`${translationPrefix}.backend`)}
							value={backend}
							data={backendOptions}
							onChange={(value) => {
								if (value === "cpu" || value === "cuda") {
									setBackend(value);
								}
							}}
						/>
						<Select
							label={t(`${translationPrefix}.source`)}
							value={source}
							data={sourceOptions}
							onChange={(value) => {
								if (value === "official" || value === "custom") {
									setSource(value);
									setAcknowledged(false);
									if (value === "official") {
										// The server rejects a commit on the official source outright — it builds the
										// engine-pinned revision — so the field is cleared with the selection rather
										// than sent and refused.
										setCommit("");
									}
								}
							}}
						/>
					</Group>

					<Text size="sm" c="dimmed" data-testid="whisper-source-build-revision-behavior">
						{t(`${translationPrefix}.revisionBehavior.${revisionKey}`)}
					</Text>

					{source === "custom" ? (
						<Stack gap="sm">
							<TextInput
								label={t(`${translationPrefix}.repository`)}
								placeholder={t(`${translationPrefix}.repositoryPlaceholder`)}
								value={repository}
								onChange={(event) => {
									setRepository(event.currentTarget.value);
									setAcknowledged(false);
								}}
							/>
							<TextInput
								label={t(`${translationPrefix}.commit`)}
								value={commit}
								onChange={(event) => setCommit(event.currentTarget.value)}
							/>
							<InlineErrorAlert message={t(`${translationPrefix}.riskWarning`)} />
							<Checkbox
								checked={acknowledged}
								onChange={(event) => setAcknowledged(event.currentTarget.checked)}
								label={t(`${translationPrefix}.riskAcknowledgement`)}
							/>
						</Stack>
					) : null}

					<SourceBuildPrerequisiteList items={prerequisites.data?.items ?? []} translationPrefix={translationPrefix} />
					<Text size="xs" c="dimmed">
						{t(`${translationPrefix}.buildCost`)}
					</Text>

					{validationError ? <Alert color="yellow">{validationError}</Alert> : null}

					<Group>
						<Button
							leftSection={<IconReload size={16} />}
							onClick={run}
							loading={start.isPending}
							disabled={validationError !== null || prerequisites.data?.canBuild !== true || isBuilding || activity.isBusy}
						>
							{managed ? t(`${translationPrefix}.rebuild`) : t(`${translationPrefix}.build`)}
						</Button>
					</Group>
				</SourceBuildFormDisclosure>
			</Stack>

			<DialogShell
				opened={removeConfirmOpen}
				onClose={() => setRemoveConfirmOpen(false)}
				title={t(`${translationPrefix}.removeConfirmTitle`)}
				size="sm"
				enableFullScreenToggle={false}
				data-testid="whisper-source-build-remove-confirm"
				footer={
					<>
						<Button variant="default" onClick={() => setRemoveConfirmOpen(false)}>
							{t("common.cancel")}
						</Button>
						<Button color="red" onClick={confirmRemove}>
							{t(`${translationPrefix}.removeConfirm`)}
						</Button>
					</>
				}
			>
				<Text size="sm">{t(`${translationPrefix}.removeConfirmBody`)}</Text>
			</DialogShell>
		</Card>
	);
}
