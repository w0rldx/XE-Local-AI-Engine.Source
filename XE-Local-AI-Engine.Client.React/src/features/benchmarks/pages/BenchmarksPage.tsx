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
import { IconAlertTriangle, IconFlask, IconLock, IconPlus, IconRefresh, IconRocket, IconSettings } from "@tabler/icons-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { toast } from "@/core/ui/notifications/Toast";
import { useAgentDefinitions } from "@/features/agents/queries/useAgentDefinitions";
import { BenchmarkLaunchCompare } from "@/features/benchmarks/components/BenchmarkLaunchCompare";
import { BenchmarkProjectForm } from "@/features/benchmarks/components/BenchmarkProjectForm";
import { BenchmarkRunLivePane } from "@/features/benchmarks/components/BenchmarkRunLivePane";
import type { BenchmarkKvCacheType, BenchmarkProjectDraft } from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkKvCacheTypes, isUnsupportedKvCacheTypeError } from "@/features/benchmarks/models/BenchmarkModels";
import {
	useBenchmarkProject,
	useBenchmarkProjects,
	useBenchmarkRuns,
	useCreateBenchmarkProject,
	useEligibleBenchmarkModels,
	useStartBenchmarkRun,
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
	judgePromptVersion: 1,
	judgeOutputSchemaVersion: 1,
};

type EditorMode = "create" | "edit" | null;
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
	const [selectedModel, setSelectedModel] = useState<string | null>(null);
	const [selectedKvCacheType, setSelectedKvCacheType] = useState<BenchmarkKvCacheType | typeof autoKvCacheType>(autoKvCacheType);
	const [selectedRunIds, setSelectedRunIds] = useState<string[]>([]);
	const projectQuery = useBenchmarkProject(selectedProjectId);
	const runsQuery = useBenchmarkRuns(selectedProjectId);
	const modelsQuery = useEligibleBenchmarkModels(projectQuery.data?.contextTokens);
	const allModelsQuery = useEligibleBenchmarkModels();
	const agentsQuery = useAgentDefinitions();
	const createProject = useCreateBenchmarkProject();
	const updateProject = useUpdateBenchmarkProject();
	const startRun = useStartBenchmarkRun();

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
		const runs = runsQuery.data ?? [];
		return [baseModelName, tunedModelName]
			.map((name) => (name == null ? undefined : runs.find((run) => run.primaryModelName === name)?.id))
			.filter((id): id is string => id != null);
	}, [runsQuery.data, baseModelName, tunedModelName]);

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
		const latest = runsQuery.data?.slice(0, 2).map((run) => run.id) ?? [];
		setSelectedRunIds((current) => {
			const valid = current.filter((id) => runsQuery.data?.some((run) => run.id === id));
			if (latest[0] && !valid.includes(latest[0])) {
				return [latest[0], ...valid].slice(0, 2);
			}
			return valid.length > 0 ? valid.slice(0, 2) : latest;
		});
	}, [runsQuery.data, linkedRunIds]);

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
						judgeEnabled: detail.judgeEnabled,
						judgeModelName: detail.judgeModelName,
						judgeContextTokens: detail.judgeContextTokens,
						judgePromptVersion: detail.judgePromptVersion,
						judgeOutputSchemaVersion: detail.judgeOutputSchemaVersion,
					}
				: emptyProject,
		[detail],
	);
	const editorDraft = editorMode === "edit" ? editDraft : emptyProject;
	const saveProject = (draft: BenchmarkProjectDraft): void => {
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
									<Button variant="default" leftSection={<IconSettings size={16} />} onClick={() => setEditorMode("edit")}>
										{detail.isFrozen ? t("common.view", "View") : t("common.edit", "Edit")}
									</Button>
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

			{runsQuery.data && runsQuery.data.length > 0 ? (
				<SectionCard title={t("pages.benchmarks.runs", "Runs")}>
					<Group gap="xs" role="group" aria-label={t("pages.benchmarks.run.compareSelection", "Runs to compare")}>
						{runsQuery.data.map((run) => (
							<Button
								key={run.id}
								size="xs"
								variant={selectedRunIds.includes(run.id) ? "filled" : "default"}
								onClick={() => toggleRun(run.id)}
							>
								{run.primaryModelName}
							</Button>
						))}
					</Group>
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
					disabled={editorMode === "edit" && detail?.isFrozen}
					isSaving={createProject.isPending || updateProject.isPending}
					onSubmit={saveProject}
					onCancel={() => setEditorMode(null)}
				/>
			</DialogShell>
		</PageShell>
	);
}
