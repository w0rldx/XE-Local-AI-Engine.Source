import { Alert, Badge, Button, Card, Grid, Group, Loader, Select, Stack, Text, Title, UnstyledButton } from "@mantine/core";
import {
	IconAlertTriangle,
	IconFlask,
	IconLayoutGrid,
	IconLock,
	IconPlus,
	IconRefresh,
	IconRocket,
	IconScale,
	IconSettings,
} from "@tabler/icons-react";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { toast } from "@/core/ui/notifications/Toast";
import { BenchmarkBatchProgressAlert } from "@/features/benchmarks/components/BenchmarkBatchProgressAlert";
import { BenchmarkExportButtons } from "@/features/benchmarks/components/BenchmarkExportButtons";
import { BenchmarkFidelityPanel } from "@/features/benchmarks/components/BenchmarkFidelityPanel";
import { BenchmarkRepeatModePicker } from "@/features/benchmarks/components/BenchmarkRepeatModePicker";
import { BenchmarkTaskItemEditor } from "@/features/benchmarks/components/BenchmarkTaskItemEditor";
import type { BenchmarksPageController } from "@/features/benchmarks/hooks/useBenchmarksPageController";
import { benchmarkKvCacheTypes } from "@/features/benchmarks/models/BenchmarkModels";
import { formatBenchmarkDuration } from "@/features/benchmarks/models/BenchmarkRunEstimate";
import { isVerifiableCriterionKind, toBenchmarkCriterionKind } from "@/features/benchmarks/models/BenchmarkVerifier";

const autoKvCacheType = "auto" as const;

export function BenchmarkProjectWorkspace({ controller }: { readonly controller: BenchmarksPageController }) {
	const {
		t,
		projectsQuery,
		selectedProjectId,
		selectProject,
		setEditorMode,
		projectQuery,
		detail,
		judgeAttemptsActive,
		affectedRunCount,
		rejudgeProject,
		setConfirmMode,
		judgeFamilyOverlap,
		familyWarningDismissedFor,
		setFamilyWarningDismissedFor,
		selectedModel,
		selectModel,
		modelsQuery,
		selectedKvCacheType,
		setSelectedKvCacheType,
		allModelsQuery,
		startRun,
		repeatMode,
		answerVarianceTemperature,
		setRepeatMode,
		setAnswerVarianceTemperature,
		setMatrixRejections,
		setMatrixOpen,
		batchProgress,
		setBatchLaunch,
		selectRun,
		startRunErrorMessage,
		runs,
		singleRunEstimate,
	} = controller;
	return (
		<>
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
								onClick={() => selectProject(project.id)}
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
										<BenchmarkExportButtons projectId={detail.id} />
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
								{detail.judge.enabled && detail.judge.promptVersionOutdated ? (
									<Alert color="yellow" icon={<IconAlertTriangle size={16} />} data-testid="benchmark-judge-prompt-outdated">
										<Group justify="space-between" align="center" wrap="nowrap">
											<Text size="sm">
												{t(
													"pages.benchmarks.judge.promptVersionOutdated",
													"Judge prompt version outdated — re-save the judge to upgrade (forces re-judge).",
												)}
											</Text>
											<Button
												variant="default"
												size="xs"
												leftSection={<IconSettings size={14} />}
												onClick={() => setEditorMode("edit")}
												data-testid="benchmark-judge-prompt-outdated-edit"
											>
												{t("pages.benchmarks.project.editJudge", "Edit judge")}
											</Button>
										</Group>
									</Alert>
								) : null}
								{judgeFamilyOverlap && familyWarningDismissedFor !== detail.id ? (
									<Alert
										color="yellow"
										icon={<IconAlertTriangle size={16} />}
										withCloseButton={true}
										closeButtonLabel={t("common.close", "Close")}
										onClose={() => setFamilyWarningDismissedFor(detail.id)}
										data-testid="benchmark-judge-family-warning"
									>
										{t(
											"pages.benchmarks.judge.familyWarning",
											"Judge model family '{{family}}' matches {{matches}} primary run(s); self-preference bias possible.",
											{ family: judgeFamilyOverlap.family, matches: judgeFamilyOverlap.matchCount },
										)}
									</Alert>
								) : null}
								<BenchmarkTaskItemEditor
									projectId={detail.id}
									projectContextTokens={detail.contextTokens}
									hasRuns={runs.length > 0}
									criteria={(detail.judge.rubric?.criteria ?? []).filter((criterion) =>
										isVerifiableCriterionKind(toBenchmarkCriterionKind(criterion.kind)),
									)}
								/>
								<Group grow={true} align="flex-end">
									<Select
										label={t("pages.benchmarks.run.model", "Primary model")}
										searchable={true}
										value={selectedModel}
										onChange={selectModel}
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
													repeatMode,
													answerVarianceTemperature: repeatMode === "AnswerVariance" ? answerVarianceTemperature : null,
												},
												{
													onSuccess: (run) => selectRun(run.id),
													onError: (error) => toast.error(startRunErrorMessage(error)),
												},
											)
										}
									>
										{t("pages.benchmarks.run.start", "Start run")}
									</Button>
									<Button
										variant="default"
										leftSection={<IconLayoutGrid size={16} />}
										onClick={() => {
											setMatrixRejections([]);
											setMatrixOpen(true);
										}}
										data-testid="benchmark-open-matrix"
									>
										{t("pages.benchmarks.matrix.open", "Batch runs…")}
									</Button>
								</Group>
								<Text size="xs" c="dimmed" data-testid="benchmark-single-run-estimate">
									{t("pages.benchmarks.run.estimate", "One start = {{count}} runs, one per task item", {
										count: singleRunEstimate.totalRuns,
									})}
									{singleRunEstimate.estimatedMs === null
										? ""
										: ` · ${t("pages.benchmarks.matrix.estimate", "about {{duration}}", {
												duration: formatBenchmarkDuration(singleRunEstimate.estimatedMs),
											})}`}
								</Text>
								<BenchmarkFidelityPanel
									projectId={detail.id}
									fidelity={detail.fidelity}
									projectVersion={detail.version}
									models={allModelsQuery.data ?? []}
								/>
								<BenchmarkRepeatModePicker
									mode={repeatMode}
									temperature={answerVarianceTemperature}
									onChange={(mode, temperature) => {
										setRepeatMode(mode);
										setAnswerVarianceTemperature(temperature);
									}}
								/>
								{/* Gone on its own once every started run is terminal — a progress line with nothing left to
							    report is just clutter the operator has to close. */}
								{batchProgress && batchProgress.done < batchProgress.total ? (
									<BenchmarkBatchProgressAlert progress={batchProgress} onDismiss={() => setBatchLaunch(null)} />
								) : null}
							</Stack>
						) : !projectQuery.isLoading ? (
							<Text c="dimmed">{t("pages.benchmarks.project.select", "Select a benchmark project.")}</Text>
						) : null}
					</SectionCard>
				</Grid.Col>
			</Grid>
		</>
	);
}
