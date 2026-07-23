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
import { IconAlertTriangle, IconCircleCheck, IconCircleX, IconPlayerStop, IconReload, IconTrash } from "@tabler/icons-react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { toast } from "@/core/ui/notifications/Toast";
import { CudaBuildLogView } from "@/features/node-settings/components/CudaBuildLogView";
import { useSourceBuildHub } from "@/features/node-settings/hooks/useSourceBuildHub";
import type {
	LlamaCppSourceBackend,
	LlamaCppSourceSelection,
	SourceBuildDraft,
} from "@/features/node-settings/models/SourceBuildModels";
import {
	mergeSourceBuildLogs,
	sourceBuildIdentity,
	sourceBuildLogEntries,
	sourceBuildPrerequisiteDiagnostic,
	sourceBuildValidationIssue,
} from "@/features/node-settings/models/SourceBuildModels";
import {
	useCancelSourceBuild,
	useLlamaCppRuntimeStatus,
	useRemoveSourceBuild,
	useSourceBuildPrerequisites,
	useSourceBuildStatus,
	useStartSourceBuild,
} from "@/features/node-settings/queries/useLocalRuntime";

export function SourceBuildCard() {
	const { t } = useTranslation();
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const [backend, setBackend] = useState<LlamaCppSourceBackend>("cpu");
	const [source, setSource] = useState<LlamaCppSourceSelection>("official");
	const [repository, setRepository] = useState("");
	const [commit, setCommit] = useState("");
	const [acknowledged, setAcknowledged] = useState(false);
	const prerequisites = useSourceBuildPrerequisites(backend, developerMode);
	const status = useSourceBuildStatus(developerMode);
	const runtime = useLlamaCppRuntimeStatus(developerMode);
	const start = useStartSourceBuild();
	const cancel = useCancelSourceBuild();
	const remove = useRemoveSourceBuild();
	const hub = useSourceBuildHub(developerMode);

	const installed = runtime.data?.installed;
	useEffect(() => {
		if (!installed?.isSourceBuild) {
			return;
		}
		if (installed.variant === "cpu" || installed.variant === "vulkan" || installed.variant === "cuda") {
			setBackend(installed.variant);
		}
		setAcknowledged(false);
		const installedSource =
			installed.sourceSelection ??
			(installed.sourceRepository && installed.sourceRepository !== "https://github.com/ggml-org/llama.cpp"
				? "custom"
				: "official");
		if (installedSource === "custom") {
			setSource("custom");
			setRepository(installed.sourceRepository ?? "");
		} else {
			setSource("official");
			setRepository("");
		}
		setCommit(
			installedSource === "custom" && installed.sourceRevisionMode === "explicitCommit"
				? (installed.sourceRequestedCommit ?? "")
				: "",
		);
	}, [installed]);

	if (!developerMode) {
		return null;
	}

	const draft: SourceBuildDraft = { backend, source, repository, commit, acknowledgeCustomSourceRisk: acknowledged };
	const validationIssue = sourceBuildValidationIssue(draft);
	const validationError =
		validationIssue === null ? null : t(`pages.nodeSettings.llamaCpp.sourceBuild.validation.${validationIssue}`);
	const buildStatus = status.data;
	const isBuilding = buildStatus?.isRunning === true || start.isPending;
	const runningProcesses = runtime.data?.runningProcessCount ?? 0;
	const current = buildStatus?.currentBuild;
	const activeRepository = installed?.sourceRepository ?? null;
	const activeSourceSelection =
		installed?.sourceSelection ??
		(activeRepository && activeRepository !== "https://github.com/ggml-org/llama.cpp" ? "custom" : "official");
	const activeRevisionMode = installed?.sourceRevisionMode ?? null;
	const livePhase = hub.phase ?? buildStatus?.phase ?? null;
	const persistedIdentity = sourceBuildIdentity(current);
	const liveLogEntries =
		hub.buildIdentity === null || hub.buildIdentity === persistedIdentity
			? mergeSourceBuildLogs(
					sourceBuildLogEntries(buildStatus?.logStartSequence ?? 0, buildStatus?.logLines ?? []),
					hub.logEntries,
				)
			: hub.logEntries;
	const liveLogs = liveLogEntries.map((entry) => entry.message);
	const liveError = hub.error ?? buildStatus?.sanitizedError ?? null;
	const backendOptions = (["cpu", "vulkan", "cuda"] as const).map((value) => ({
		value,
		label: t(`pages.nodeSettings.llamaCpp.sourceBuild.backends.${value}`),
	}));
	const sourceOptions = (["official", "custom"] as const).map((value) => ({
		value,
		label: t(`pages.nodeSettings.llamaCpp.sourceBuild.sources.${value}`),
	}));

	const run = (): void => {
		hub.reset();
		start.mutate(draft, {
			onError: (error) => toast.error(apiErrorMessage(error, t("pages.nodeSettings.llamaCpp.sourceBuild.startError"))),
		});
		if (source === "custom") {
			setAcknowledged(false);
		}
	};

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="source-build-card">
			<Stack gap="md">
				<Group gap="xs">
					<Title order={4}>{t("pages.nodeSettings.llamaCpp.sourceBuild.title")}</Title>
					<Badge color="grape">{t("pages.nodeSettings.llamaCpp.sourceBuild.devBadge")}</Badge>
				</Group>
				<Text size="sm" c="dimmed">
					{t("pages.nodeSettings.llamaCpp.sourceBuild.description")}
				</Text>
				<Group grow={true} align="start">
					<Select
						label={t("pages.nodeSettings.llamaCpp.sourceBuild.backend")}
						value={backend}
						data={backendOptions}
						onChange={(value) => value && setBackend(value as LlamaCppSourceBackend)}
					/>
					<Select
						label={t("pages.nodeSettings.llamaCpp.sourceBuild.source")}
						value={source}
						data={sourceOptions}
						onChange={(value) => {
							if (value) {
								setSource(value as LlamaCppSourceSelection);
								setAcknowledged(false);
								if (value === "official") {
									setCommit("");
								}
							}
						}}
					/>
				</Group>
				{source === "custom" ? (
					<Stack gap="sm">
						<TextInput
							label={t("pages.nodeSettings.llamaCpp.sourceBuild.repository")}
							placeholder={t("pages.nodeSettings.llamaCpp.sourceBuild.repositoryPlaceholder")}
							value={repository}
							onChange={(event) => {
								setRepository(event.currentTarget.value);
								setAcknowledged(false);
							}}
						/>
						<Alert color="red" icon={<IconAlertTriangle size={16} />}>
							{t("pages.nodeSettings.llamaCpp.sourceBuild.riskWarning")}
						</Alert>
						<Checkbox
							checked={acknowledged}
							onChange={(event) => setAcknowledged(event.currentTarget.checked)}
							label={t("pages.nodeSettings.llamaCpp.sourceBuild.riskAcknowledgement")}
						/>
					</Stack>
				) : null}
				{source === "custom" ? (
					<TextInput
						label={t("pages.nodeSettings.llamaCpp.sourceBuild.commit")}
						value={commit}
						onChange={(event) => setCommit(event.currentTarget.value)}
					/>
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
									{t(`pages.nodeSettings.llamaCpp.sourceBuild.prerequisites.${item.key}`, item.key)}
								</Text>{" "}
								<Text span={true} c="dimmed">
									{t(
										`pages.nodeSettings.llamaCpp.sourceBuild.prerequisiteAvailability.${item.satisfied ? "available" : "missing"}`,
									)}
									{diagnostic ? ` · ${diagnostic}` : ""}
								</Text>
							</List.Item>
						);
					})}
				</List>
				{validationError ? <Alert color="yellow">{validationError}</Alert> : null}
				{liveError ? <Alert color="red">{liveError}</Alert> : null}
				{isBuilding ? <CudaBuildLogView phase={livePhase} logLines={liveLogs} /> : null}
				{installed?.isSourceBuild ? (
					<Stack gap="xs">
						<Group>
							<Badge color="green">{t("pages.nodeSettings.llamaCpp.sourceBuild.active", { backend: installed.variant })}</Badge>
							{installed.sourceCommit ? <Text ff="monospace">{installed.sourceCommit.slice(0, 12)}</Text> : null}
						</Group>
						{activeRepository ? (
							<Text size="sm" c="dimmed">
								{activeRepository} · {t(`pages.nodeSettings.llamaCpp.sourceBuild.sources.${activeSourceSelection}`)} ·{" "}
								{activeRevisionMode ? t(`pages.nodeSettings.llamaCpp.sourceBuild.revisions.${activeRevisionMode}`) : ""}
							</Text>
						) : null}
					</Stack>
				) : null}
				<Group>
					<Button
						leftSection={<IconReload size={16} />}
						onClick={run}
						loading={start.isPending}
						disabled={validationError !== null || prerequisites.data?.canBuild !== true || isBuilding || runningProcesses > 0}
					>
						{installed?.isSourceBuild
							? t("pages.nodeSettings.llamaCpp.sourceBuild.rebuild")
							: t("pages.nodeSettings.llamaCpp.sourceBuild.build")}
					</Button>
					{isBuilding ? (
						<Button
							color="yellow"
							leftSection={<IconPlayerStop size={16} />}
							onClick={() => cancel.mutate()}
							loading={cancel.isPending}
						>
							{t("pages.nodeSettings.llamaCpp.sourceBuild.cancel")}
						</Button>
					) : null}
					{installed?.isSourceBuild ? (
						<Button
							color="red"
							variant="light"
							leftSection={<IconTrash size={16} />}
							disabled={isBuilding || runningProcesses > 0}
							onClick={() =>
								remove.mutate(undefined, {
									onError: (error) => toast.error(apiErrorMessage(error, t("pages.nodeSettings.llamaCpp.sourceBuild.removeError"))),
								})
							}
						>
							{t("pages.nodeSettings.llamaCpp.sourceBuild.remove")}
						</Button>
					) : null}
				</Group>
			</Stack>
		</Card>
	);
}
