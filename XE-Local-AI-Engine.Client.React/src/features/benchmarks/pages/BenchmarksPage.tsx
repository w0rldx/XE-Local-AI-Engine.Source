import {
	Alert,
	Badge,
	Button,
	Card,
	Grid,
	Group,
	Loader,
	Select,
	SimpleGrid,
	Stack,
	Text,
	Title,
	UnstyledButton,
} from "@mantine/core";
import {
	IconAlertTriangle,
	IconFlask,
	IconLock,
	IconPlus,
	IconRefresh,
	IconRocket,
	IconScale,
	IconSettings,
} from "@tabler/icons-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import type { XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkJudgePolicyDraftDto as JudgePolicyDraft } from "@/core/api/generated";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { toast } from "@/core/ui/notifications/Toast";
import { useAgentDefinitions } from "@/features/agents/queries/useAgentDefinitions";
import { BenchmarkLaunchCompare } from "@/features/benchmarks/components/BenchmarkLaunchCompare";
import { BenchmarkProjectForm } from "@/features/benchmarks/components/BenchmarkProjectForm";
import { BenchmarkRunLivePane } from "@/features/benchmarks/components/BenchmarkRunLivePane";
import { BenchmarkRunsTable } from "@/features/benchmarks/components/BenchmarkRunsTable";
import type { BenchmarkKvCacheType, BenchmarkProjectDraft, BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";
import {
	benchmarkErrorCode,
	benchmarkKvCacheTypes,
	isUnsupportedKvCacheTypeError,
} from "@/features/benchmarks/models/BenchmarkModels";
import { hasActiveJudgeAttempt, succeededRunCount } from "@/features/benchmarks/models/BenchmarkRanking";
import {
	useBenchmarkProject,
	useBenchmarkProjects,
	useBenchmarkRubricPresets,
	useBenchmarkRuns,
	useCreateBenchmarkProject,
	useDeleteBenchmarkRun,
	useEligibleBenchmarkModels,
	useRejudgeBenchmarkProject,
	useRejudgeBenchmarkRun,
	useStartBenchmarkRun,
	useUpdateBenchmarkJudgePolicy,
	useUpdateBenchmarkProject,
} from "@/features/benchmarks/queries/useBenchmarks";

const emptyProject: BenchmarkProjectDraft = {
	name: "",
	coreTask: "",
	contextTokens: 4096,
	agentDefinitionId: "",
	judgeEnabled: false,
	judgeModelName: null,
	judgeContextTokens: null,
	rubric: null,
	referenceAnswer: null,
};

type EditorMode = "create" | "edit" | null;
/** Which confirmation the operator is being asked for; both re-score every succeeded run of the project. */
type ConfirmMode = "judgePolicy" | "rejudgeAll" | null;
/** The picker's "Auto" entry is a UI-only value: it is sent as an omitted `kvCacheType`, which the node resolves. */
const autoKvCacheType = "auto";

interface BenchmarksPageProps {
	/** Model names deep-linked from a training comparison report; the matching runs open selected. */
	baseModelName?: string;
	tunedModelName?: string;
}

export function BenchmarksPage({ baseModelName, tunedModelName }: BenchmarksPageProps = {}) {
	const { t } = useTranslation();
	const projectsQuery = useBenchmarkProjects();
	const [selectedProjectId, setSelectedProjectId] = useState<string | null>(null);
	const [editorMode, setEditorMode] = useState<EditorMode>(null);
	const [confirmMode, setConfirmMode] = useState<ConfirmMode>(null);
	const [pendingPolicy, setPendingPolicy] = useState<JudgePolicyDraft | null>(null);
	const [selectedModel, setSelectedModel] = useState<string | null>(null);
	const [selectedKvCacheType, setSelectedKvCacheType] = useState<BenchmarkKvCacheType | typeof autoKvCacheType>(autoKvCacheType);
	const [selectedRunIds, setSelectedRunIds] = useState<string[]>([]);
	const projectQuery = useBenchmarkProject(selectedProjectId);
	const runsQuery = useBenchmarkRuns(selectedProjectId);
	const modelsQuery = useEligibleBenchmarkModels(projectQuery.data?.contextTokens);
	const allModelsQuery = useEligibleBenchmarkModels();
	const agentsQuery = useAgentDefinitions();
	const presetsQuery = useBenchmarkRubricPresets(editorMode !== null);
	const createProject = useCreateBenchmarkProject();
	const updateProject = useUpdateBenchmarkProject();
	const updateJudge = useUpdateBenchmarkJudgePolicy();
	const rejudgeProject = useRejudgeBenchmarkProject();
	const rejudgeRun = useRejudgeBenchmarkRun();
	const deleteRun = useDeleteBenchmarkRun();
	const startRun = useStartBenchmarkRun();
	const runs = useMemo(() => runsQuery.data?.items ?? [], [runsQuery.data]);

	useEffect(() => {
		if (!selectedProjectId && projectsQuery.data?.[0]) {
			setSelectedProjectId(projectsQuery.data[0].id);
		}
		if (selectedProjectId && projectsQuery.data && !projectsQuery.data.some((project) => project.id === selectedProjectId)) {
			setSelectedProjectId(projectsQuery.data[0]?.id ?? null);
		}
	}, [projectsQuery.data, selectedProjectId]);
	const linkedRunsApplied = useRef(false);
	// Runs the deep link asks for, newest first per model name. Empty unless the search carries names that match.
	const linkedRunIds = useMemo(() => {
		return [baseModelName, tunedModelName]
			.map((name) => (name == null ? undefined : runs.find((run) => run.primaryModelName === name)?.id))
			.filter((id): id is string => id != null);
	}, [runs, baseModelName, tunedModelName]);

	useEffect(() => {
		// A deep link is an explicit request for two specific runs, applied once. It also suspends the "always keep the
		// newest run selected" rule below: otherwise the next poll would push one of the two requested runs back out.
		if (linkedRunIds.length > 0) {
			if (!linkedRunsApplied.current) {
				linkedRunsApplied.current = true;
				setSelectedRunIds(linkedRunIds);
			}
			return;
		}
		const latest = runs.slice(0, 2).map((run) => run.id);
		setSelectedRunIds((current) => {
			const valid = current.filter((id) => runs.some((run) => run.id === id));
			if (latest[0] && !valid.includes(latest[0])) {
				return [latest[0], ...valid].slice(0, 2);
			}
			return valid.length > 0 ? valid.slice(0, 2) : latest;
		});
	}, [runs, linkedRunIds]);

	// The pick belongs to one prospective run; a different model or project is a different run, so it falls back to Auto
	// rather than silently carrying a quantized type onto a model that may not support it.
	// biome-ignore lint/correctness/useExhaustiveDependencies: resetting is the effect, the pick itself is not an input.
	useEffect(() => setSelectedKvCacheType(autoKvCacheType), [selectedModel, selectedProjectId]);

	const detail = projectQuery.data;
	const editDraft = useMemo<BenchmarkProjectDraft>(
		() =>
			detail
				? {
						name: detail.name,
						coreTask: detail.coreTask,
						contextTokens: detail.contextTokens,
						agentDefinitionId: detail.agentDefinitionId,
						judgeEnabled: detail.judge.enabled,
						judgeModelName: detail.judge.modelName,
						judgeContextTokens: detail.judge.requestedContextTokens,
						rubric: detail.judge.rubric,
						referenceAnswer: detail.judge.referenceAnswer,
					}
				: emptyProject,
		[detail],
	);
	const editorDraft = editorMode === "edit" ? editDraft : emptyProject;
	const judgeAttemptsActive = hasActiveJudgeAttempt(runs);
	const affectedRunCount = succeededRunCount(runs);

	// A judge change on a frozen project is refused until the operator confirms the re-judge it implies, and while any
	// judging of the project is still running. Both come back as ProblemDetails 409s with their own code.
	const saveJudgePolicy = (policy: JudgePolicyDraft | null, confirmRejudge: boolean): void => {
		if (!detail) {
			return;
		}
		updateJudge.mutate(
			{ projectId: detail.id, expectedVersion: detail.version, policy, confirmRejudge },
			{
				onSuccess: () => {
					setEditorMode(null);
					setConfirmMode(null);
					setPendingPolicy(null);
				},
				onError: (error) => {
					const code = benchmarkErrorCode(error);
					if (code === "RejudgeRequired") {
						setPendingPolicy(policy);
						setConfirmMode("judgePolicy");
						return;
					}
					if (code === "JudgeAttemptsActive") {
						toast.error(
							t(
								"pages.benchmarks.errors.judgeAttemptsActive",
								"A judging of this project is still running. Wait for it or cancel it first.",
							),
						);
						return;
					}
					toast.error(apiErrorMessage(error, t("pages.benchmarks.errors.projectSave", "Could not save the project.")));
				},
			},
		);
	};

	const saveProject = (draft: BenchmarkProjectDraft): void => {
		if (editorMode === "edit" && detail?.isFrozen) {
			saveJudgePolicy(
				draft.judgeEnabled
					? {
							modelName: draft.judgeModelName ?? "",
							contextTokens: draft.judgeContextTokens ?? 0,
							rubric: draft.rubric,
							referenceAnswer: draft.referenceAnswer,
						}
					: null,
				false,
			);
			return;
		}
		if (editorMode === "edit" && detail) {
			updateProject.mutate(
				{ projectId: detail.id, expectedVersion: detail.version, draft },
				{
					onSuccess: () => setEditorMode(null),
					onError: (error) =>
						toast.error(apiErrorMessage(error, t("pages.benchmarks.errors.projectSave", "Could not save the project."))),
				},
			);
			return;
		}
		createProject.mutate(draft, {
			onSuccess: (project) => {
				setSelectedProjectId(project.id);
				setEditorMode(null);
			},
			onError: (error) =>
				toast.error(apiErrorMessage(error, t("pages.benchmarks.errors.projectSave", "Could not save the project."))),
		});
	};
	// A 422 is the node refusing this KV type for this runtime (quantized KV on CPU, or a binary whose manifest does not
	// support it). Its sanitized reason is the useful half; the hint says what actually gets the run started. A local
	// response-validation failure reuses 422 as its status, and telling the operator to pick f16 would be nonsense there.
	const startRunErrorMessage = (error: unknown): string => {
		const message = apiErrorMessage(error, t("pages.benchmarks.errors.start", "Could not start the benchmark run."));
		return isUnsupportedKvCacheTypeError(error)
			? `${message} ${t("pages.benchmarks.errors.kvUnsupportedHint", "Pick f16 explicitly to run this model on this runtime.")}`
			: message;
	};
	const toggleRun = (id: string): void => {
		setSelectedRunIds((current) => (current.includes(id) ? current.filter((item) => item !== id) : [id, ...current].slice(0, 2)));
	};
	const rejudgeAll = (): void => {
		if (!detail) {
			return;
		}
		rejudgeProject.mutate(
			{ projectId: detail.id, expectedVersion: detail.version },
			{
				onSuccess: () => setConfirmMode(null),
				onError: (error) => {
					setConfirmMode(null);
					toast.error(
						benchmarkErrorCode(error) === "JudgeAttemptsActive"
							? t(
									"pages.benchmarks.errors.judgeAttemptsActive",
									"A judging of this project is still running. Wait for it or cancel it first.",
								)
							: apiErrorMessage(error, t("pages.benchmarks.errors.rejudgeProject", "Could not re-judge this project.")),
					);
				},
			},
		);
	};
	const rejudgeOne = (run: BenchmarkRunSummary): void => {
		rejudgeRun.mutate(
			{ run, force: true },
			{
				onError: (error) =>
					toast.error(apiErrorMessage(error, t("pages.benchmarks.errors.rejudgeRun", "Could not re-judge this run."))),
			},
		);
	};
	const removeRun = (run: BenchmarkRunSummary): void => {
		deleteRun.mutate(run, {
			onError: (error) =>
				toast.error(apiErrorMessage(error, t("pages.benchmarks.errors.delete", "Could not delete this terminal run."))),
		});
	};

	return (
		<PageShell>
			<PageHeader
				title={t("pages.benchmarks.title", "Local model benchmarks")}
				icon={<IconFlask size={24} />}
				subtitle={t(
					"pages.benchmarks.subtitle",
					"Compare local models against one frozen agent task, with optional independent judging.",
				)}
				actions={
					<Button leftSection={<IconPlus size={16} />} onClick={() => setEditorMode("create")}>
						{t("pages.benchmarks.project.create", "New project")}
					</Button>
				}
			/>

			<Grid gap="lg">
				<Grid.Col span={{ base: 12, md: 4 }}>
					<SectionCard
						gap="sm"
						title={t("pages.benchmarks.projects", "Projects")}
						actions={
							<Button variant="subtle" size="xs" leftSection={<IconRefresh size={14} />} onClick={() => projectsQuery.refetch()}>
								{t("common.refresh", "Refresh")}
							</Button>
						}
					>
						{projectsQuery.isLoading ? (
							<Group gap="sm">
								<Loader size="sm" />
								<Text c="dimmed">{t("pages.benchmarks.loading.projects", "Loading benchmark projects…")}</Text>
							</Group>
						) : null}
						{projectsQuery.error ? (
							<Alert color="red" icon={<IconAlertTriangle size={16} />}>
								{apiErrorMessage(
									projectsQuery.error,
									t("pages.benchmarks.errors.projectsLoad", "Could not load benchmark projects."),
								)}
							</Alert>
						) : null}
						{projectsQuery.data?.map((project) => (
							<UnstyledButton
								key={project.id}
								onClick={() => setSelectedProjectId(project.id)}
								aria-pressed={project.id === selectedProjectId}
							>
								<Card
									withBorder={true}
									bg={project.id === selectedProjectId ? "var(--mantine-color-blue-light)" : undefined}
									padding="sm"
								>
									<Group justify="space-between">
										<Text fw={700}>{project.name}</Text>
										{project.isFrozen ? (
											<Badge leftSection={<IconLock size={11} />}>{t("pages.benchmarks.project.frozen", "Frozen")}</Badge>
										) : null}
									</Group>
									<Text size="xs" c="dimmed">
										{t("pages.benchmarks.project.runCount", "{{count}} runs", { count: project.runCount })}
									</Text>
								</Card>
							</UnstyledButton>
						))}
						{projectsQuery.data?.length === 0 ? (
							<Text c="dimmed">
								{t("pages.benchmarks.project.empty", "Create a project to freeze one task and compare models.")}
							</Text>
						) : null}
					</SectionCard>
				</Grid.Col>

				<Grid.Col span={{ base: 12, md: 8 }}>
					<SectionCard>
						{projectQuery.isLoading ? (
							<Group gap="sm">
								<Loader size="sm" />
								<Text c="dimmed">{t("pages.benchmarks.loading.project", "Loading benchmark project…")}</Text>
							</Group>
						) : null}
						{detail ? (
							<Stack gap="md">
								<Group justify="space-between" align="flex-start">
									<Stack gap={2}>
										<Title order={3}>{detail.name}</Title>
										<Text c="dimmed">{detail.coreTask}</Text>
									</Stack>
									<Group gap="xs">
										{detail.judge.enabled ? (
											<Button
												variant="default"
												leftSection={<IconScale size={16} />}
												disabled={judgeAttemptsActive || affectedRunCount === 0}
												loading={rejudgeProject.isPending}
												onClick={() => setConfirmMode("rejudgeAll")}
												data-testid="benchmark-rejudge-all"
											>
												{t("pages.benchmarks.project.rejudgeAll", "Re-judge all runs")}
											</Button>
										) : null}
										<Button variant="default" leftSection={<IconSettings size={16} />} onClick={() => setEditorMode("edit")}>
											{detail.isFrozen ? t("pages.benchmarks.project.editJudge", "Edit judge") : t("common.edit", "Edit")}
										</Button>
									</Group>
								</Group>
								{detail.isFrozen ? (
									<Alert color="blue" icon={<IconLock size={16} />}>
										{t(
											"pages.benchmarks.project.frozenExplanation",
											"This project is frozen while runs exist. Delete its terminal runs to edit it again.",
										)}
									</Alert>
								) : null}
								<Group grow={true} align="flex-end">
									<Select
										label={t("pages.benchmarks.run.model", "Primary model")}
										searchable={true}
										value={selectedModel}
										onChange={setSelectedModel}
										data={(modelsQuery.data ?? []).map((model) => ({
											value: model.modelName,
											label: `${model.modelName} · ${t(`pages.benchmarks.origin.${model.origin ?? "legacy"}`, model.origin ?? "Legacy / Unknown")}`,
										}))}
									/>
									<Select
										label={t("pages.benchmarks.run.kvCacheType", "KV cache type")}
										description={t(
											"pages.benchmarks.run.kvCacheTypeHelp",
											"Quantized types launch with flash attention on. Auto uses q8_0 on GPU when the selected binary supports it, otherwise f16.",
										)}
										allowDeselect={false}
										value={selectedKvCacheType}
										onChange={(value) =>
											setSelectedKvCacheType(benchmarkKvCacheTypes.find((type) => type === value) ?? autoKvCacheType)
										}
										data={[
											{ value: autoKvCacheType, label: t("pages.benchmarks.run.kvCacheTypeAuto", "Auto") },
											...benchmarkKvCacheTypes.map((type) => ({ value: type, label: type })),
										]}
										data-testid="benchmark-kv-cache-type"
									/>
									<Button
										leftSection={<IconRocket size={16} />}
										disabled={!selectedModel}
										loading={startRun.isPending}
										onClick={() =>
											selectedModel &&
											startRun.mutate(
												{
													projectId: detail.id,
													modelName: selectedModel,
													expectedProjectVersion: detail.version,
													kvCacheType: selectedKvCacheType === autoKvCacheType ? null : selectedKvCacheType,
												},
												{
													onSuccess: (run) => setSelectedRunIds((current) => [run.id, ...current].slice(0, 2)),
													onError: (error) => toast.error(startRunErrorMessage(error)),
												},
											)
										}
									>
										{t("pages.benchmarks.run.start", "Start run")}
									</Button>
								</Group>
							</Stack>
						) : !projectQuery.isLoading ? (
							<Text c="dimmed">{t("pages.benchmarks.project.select", "Select a benchmark project.")}</Text>
						) : null}
					</SectionCard>
				</Grid.Col>
			</Grid>

			{runsQuery.data && runs.length > 0 ? (
				<SectionCard title={t("pages.benchmarks.runs", "Runs")}>
					<BenchmarkRunsTable
						runs={runs}
						cohort={runsQuery.data.cohort}
						selectedRunIds={selectedRunIds}
						isActionPending={rejudgeRun.isPending || deleteRun.isPending}
						onToggleRun={toggleRun}
						onRejudgeRun={rejudgeOne}
						onDeleteRun={removeRun}
					/>
					{selectedRunIds.length === 2 && selectedRunIds[0] && selectedRunIds[1] ? (
						<BenchmarkLaunchCompare leftRunId={selectedRunIds[0]} rightRunId={selectedRunIds[1]} />
					) : null}
					<SimpleGrid cols={{ base: 1, lg: selectedRunIds.length > 1 ? 2 : 1 }}>
						{selectedRunIds.map((runId) => (
							<BenchmarkRunLivePane key={runId} runId={runId} />
						))}
					</SimpleGrid>
				</SectionCard>
			) : null}

			<DialogShell
				opened={editorMode !== null}
				onClose={() => setEditorMode(null)}
				title={
					editorMode === "create"
						? t("pages.benchmarks.project.create", "New project")
						: t("pages.benchmarks.project.edit", "Benchmark project")
				}
				size="lg"
			>
				{editorMode === "edit" && detail?.isFrozen ? (
					<Alert mb="md" color="blue" icon={<IconLock size={16} />}>
						{t(
							"pages.benchmarks.project.frozenExplanation",
							"This project is frozen while runs exist. Delete its terminal runs to edit it again.",
						)}
					</Alert>
				) : null}
				<BenchmarkProjectForm
					key={`${editorMode}-${detail?.id ?? "new"}`}
					initialValues={editorDraft}
					agents={(agentsQuery.data ?? []).filter((agent) => agent.kind === "Single")}
					models={allModelsQuery.data ?? []}
					presets={presetsQuery.data}
					frozen={editorMode === "edit" && detail?.isFrozen}
					isSaving={createProject.isPending || updateProject.isPending || updateJudge.isPending}
					onSubmit={saveProject}
					onCancel={() => setEditorMode(null)}
				/>
			</DialogShell>

			<DialogShell
				opened={confirmMode !== null}
				onClose={() => setConfirmMode(null)}
				title={t("pages.benchmarks.project.rejudgeConfirmTitle", "Re-judge this project?")}
				size="md"
				data-testid="benchmark-rejudge-confirm"
			>
				<Stack gap="md">
					<Text>
						{confirmMode === "judgePolicy"
							? t(
									"pages.benchmarks.project.rejudgeConfirmPolicy",
									"Changing the judge re-scores this project. All {{count}} succeeded runs will be re-judged and the ranking is rebuilt from the new cohort.",
									{ count: affectedRunCount },
								)
							: t(
									"pages.benchmarks.project.rejudgeConfirmAll",
									"All {{count}} succeeded runs will be re-judged under the current policy, and the ranked cohort moves to the current judge runtime.",
									{ count: affectedRunCount },
								)}
					</Text>
					<Group justify="flex-end">
						<Button variant="default" onClick={() => setConfirmMode(null)}>
							{t("common.cancel", "Cancel")}
						</Button>
						<Button
							loading={updateJudge.isPending || rejudgeProject.isPending}
							onClick={() => (confirmMode === "judgePolicy" ? saveJudgePolicy(pendingPolicy, true) : rejudgeAll())}
							data-testid="benchmark-rejudge-confirm-accept"
						>
							{t("pages.benchmarks.project.rejudgeConfirmAccept", "Re-judge")}
						</Button>
					</Group>
				</Stack>
			</DialogShell>
		</PageShell>
	);
}
