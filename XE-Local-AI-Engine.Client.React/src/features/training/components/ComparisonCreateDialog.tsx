import { Alert, Button, Group, Select, Stack, Text, TextInput } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { toast } from "@/core/ui/notifications/Toast";
import { useBenchmarkProjects, useBenchmarkRuns } from "@/features/benchmarks/queries/useBenchmarks";
import { useTrainingRunHub } from "@/features/training/hooks/useTrainingRunHub";
import { type EvaluationRun, isEvaluationActive, isEvaluationUsable } from "@/features/training/models/ComparisonModels";
import {
	useComparisonSuggestion,
	useCreateComparison,
	useCreateEvaluation,
	useRefreshEvaluations,
	useResumeEvaluation,
	useTrainingEvaluations,
} from "@/features/training/queries/useTrainingComparisons";
import { useTrainingRuns } from "@/features/training/queries/useTrainingRuns";

interface ComparisonCreateDialogProps {
	opened: boolean;
	onClose: () => void;
}

/**
 * Pick a training run, let its lineage fill in both sides, create whatever evaluation is still missing, then bind the
 * two into a report. The evaluations are the slow part — they load the model and replay every frozen hold-out sample —
 * so this dialog shows their live progress rather than blocking on them.
 */
export function ComparisonCreateDialog({ opened, onClose }: ComparisonCreateDialogProps) {
	const { t } = useTranslation();
	const [runId, setRunId] = useState<string | null>(null);
	const [name, setName] = useState("");
	const [baseBenchmarkRunId, setBaseBenchmarkRunId] = useState<string | null>(null);
	const [tunedBenchmarkRunId, setTunedBenchmarkRunId] = useState<string | null>(null);
	const [benchmarkProjectId, setBenchmarkProjectId] = useState<string | null>(null);

	const runsQuery = useTrainingRuns();
	const suggestionQuery = useComparisonSuggestion(runId);
	const suggestion = suggestionQuery.data ?? null;
	const refreshEvaluations = useRefreshEvaluations();

	const evaluationsQuery = useTrainingEvaluations(runId);
	const evaluations = useMemo(() => evaluationsQuery.data ?? [], [evaluationsQuery.data]);
	const active = evaluations.find((evaluation) => isEvaluationActive(evaluation.status)) ?? null;
	const pollingQuery = useTrainingEvaluations(runId, active != null);
	const rows = pollingQuery.data ?? evaluations;

	// Evaluation events ride the run's own hub group, so subscribing to the run is what makes them live.
	useTrainingRunHub(active != null ? runId : null, refreshEvaluations);

	const projectsQuery = useBenchmarkProjects();
	const benchmarkRunsQuery = useBenchmarkRuns(benchmarkProjectId);

	const createEvaluation = useCreateEvaluation();
	const resumeEvaluation = useResumeEvaluation();
	const createComparison = useCreateComparison();

	const findByModel = (modelName: string | null): EvaluationRun | null =>
		modelName == null ? null : (rows.find((evaluation) => evaluation.modelName === modelName) ?? null);
	const baseEvaluation = findByModel(suggestion?.baseModelName ?? null);
	const tunedEvaluation = findByModel(suggestion?.tunedModelName ?? null);
	const canCreate = isEvaluationUsable(baseEvaluation) && isEvaluationUsable(tunedEvaluation) && name.trim().length > 0;

	useEffect(() => {
		// The suggested name is a starting point, not a lock: replacing it only while the operator has not typed keeps
		// a hand-written name from being overwritten when the suggestion refetches.
		if (suggestion != null && name.trim().length === 0) {
			setName(`${suggestion.baseModelName ?? "base"} → ${suggestion.tunedModelName ?? "tuned"}`);
		}
	}, [suggestion, name]);

	const close = (): void => {
		setRunId(null);
		setName("");
		setBenchmarkProjectId(null);
		setBaseBenchmarkRunId(null);
		setTunedBenchmarkRunId(null);
		onClose();
	};

	const startEvaluation = (target: "Base" | "Tuned"): void => {
		if (runId == null) {
			return;
		}
		createEvaluation.mutate(
			{ body: { trainingRunId: runId, target } },
			{
				onError: (error) =>
					toast.error(apiErrorMessage(error, t("training.comparisons.create.evaluateFailed", "Could not start the evaluation."))),
			},
		);
	};

	const submit = (): void => {
		if (baseEvaluation == null || tunedEvaluation == null) {
			return;
		}
		createComparison.mutate(
			{
				body: {
					name: name.trim(),
					baseEvaluationRunId: baseEvaluation.id,
					tunedEvaluationRunId: tunedEvaluation.id,
					baseBenchmarkRunId: baseBenchmarkRunId ?? undefined,
					tunedBenchmarkRunId: tunedBenchmarkRunId ?? undefined,
					trainingRunId: runId ?? undefined,
				},
			},
			{
				onSuccess: close,
				onError: (error) =>
					toast.error(apiErrorMessage(error, t("training.comparisons.create.failed", "Could not create the comparison report."))),
			},
		);
	};

	const benchmarkRunOptions = (benchmarkRunsQuery.data?.items ?? []).map((run) => ({ value: run.id, label: run.primaryModelName }));

	return (
		<DialogShell onClose={close} opened={opened} size="lg" title={t("training.comparisons.create.title", "New comparison")}>
			<Stack gap="md">
				<Select
					data={(runsQuery.data ?? []).map((run) => ({ value: run.id, label: `${run.status} · ${run.id.slice(0, 8)}` }))}
					label={t("training.comparisons.create.run", "Training run")}
					onChange={setRunId}
					placeholder={t("training.comparisons.create.runPlaceholder", "Pick a finished training run")}
					searchable={true}
					value={runId}
				/>

				{suggestion?.unavailableReason == null ? null : (
					<Alert color="yellow" icon={<IconAlertTriangle size={16} />}>
						{suggestion.unavailableReason}
					</Alert>
				)}

				{suggestion == null ? null : (
					<Stack gap="xs">
						<EvaluationSide
							evaluation={baseEvaluation}
							label={t("training.comparisons.create.baseEvaluation", "Base evaluation")}
							modelName={suggestion.baseModelName}
							onEvaluate={() => startEvaluation("Base")}
							onResume={(id) =>
								resumeEvaluation.mutate(
									{ path: { evaluationId: id } },
									{
										onError: (error) =>
											toast.error(
												apiErrorMessage(
													error,
													t("training.comparisons.evaluation.resumeFailed", "Could not resume the evaluation."),
												),
											),
									},
								)
							}
							pending={createEvaluation.isPending}
						/>
						<EvaluationSide
							evaluation={tunedEvaluation}
							label={t("training.comparisons.create.tunedEvaluation", "Tuned evaluation")}
							modelName={suggestion.tunedModelName}
							onEvaluate={() => startEvaluation("Tuned")}
							onResume={(id) =>
								resumeEvaluation.mutate(
									{ path: { evaluationId: id } },
									{
										onError: (error) =>
											toast.error(
												apiErrorMessage(
													error,
													t("training.comparisons.evaluation.resumeFailed", "Could not resume the evaluation."),
												),
											),
									},
								)
							}
							pending={createEvaluation.isPending}
						/>
					</Stack>
				)}

				<TextInput
					label={t("training.comparisons.create.name", "Report name")}
					onChange={(event) => setName(event.currentTarget.value)}
					value={name}
				/>

				<Stack gap="xs">
					<Text c="dimmed" size="sm">
						{t("training.comparisons.create.benchmarkPairing", "Optional benchmark pairing")}
					</Text>
					<Select
						clearable={true}
						data={(projectsQuery.data ?? []).map((project) => ({ value: project.id, label: project.name }))}
						label={t("training.comparisons.create.benchmarkProject", "Benchmark project")}
						onChange={(value) => {
							setBenchmarkProjectId(value);
							setBaseBenchmarkRunId(null);
							setTunedBenchmarkRunId(null);
						}}
						value={benchmarkProjectId}
					/>
					<Group grow={true}>
						<Select
							clearable={true}
							data={benchmarkRunOptions}
							disabled={benchmarkProjectId == null}
							label={t("training.comparisons.create.baseBenchmark", "Base benchmark run")}
							onChange={setBaseBenchmarkRunId}
							value={baseBenchmarkRunId}
						/>
						<Select
							clearable={true}
							data={benchmarkRunOptions}
							disabled={benchmarkProjectId == null}
							label={t("training.comparisons.create.tunedBenchmark", "Tuned benchmark run")}
							onChange={setTunedBenchmarkRunId}
							value={tunedBenchmarkRunId}
						/>
					</Group>
				</Stack>

				{canCreate || suggestion == null ? null : (
					<Text c="dimmed" size="xs">
						{t(
							"training.comparisons.create.needsBothEvaluations",
							"Both sides need a finished evaluation before a report can be created.",
						)}
					</Text>
				)}

				<Group justify="flex-end">
					<Button onClick={close} variant="default">
						{t("training.comparisons.create.cancel", "Cancel")}
					</Button>
					<Button disabled={!canCreate} loading={createComparison.isPending} onClick={submit}>
						{t("training.comparisons.create.submit", "Create report")}
					</Button>
				</Group>
			</Stack>
		</DialogShell>
	);
}

interface EvaluationSideProps {
	label: string;
	modelName: string | null;
	evaluation: EvaluationRun | null;
	pending: boolean;
	onEvaluate: () => void;
	onResume: (evaluationId: string) => void;
}

function EvaluationSide({ label, modelName, evaluation, pending, onEvaluate, onResume }: EvaluationSideProps) {
	const { t } = useTranslation();

	return (
		<Group gap="sm" justify="space-between">
			<Stack gap={0}>
				<Text size="sm">{label}</Text>
				<Text c="dimmed" size="xs">
					{modelName ?? t("training.comparisons.create.missing", "Not evaluated yet")}
				</Text>
			</Stack>
			{evaluation == null ? (
				<Button disabled={modelName == null} loading={pending} onClick={onEvaluate} size="compact-sm" variant="light">
					{t("training.comparisons.create.evaluate", "Evaluate")}
				</Button>
			) : (
				<Group gap="xs">
					<Text size="xs">
						{t("training.comparisons.evaluation.progress", "{{scored}} of {{total}} scored, {{passed}} passed", {
							scored: evaluation.scoredCount,
							total: evaluation.totalCount,
							passed: evaluation.passedCount,
						})}
					</Text>
					<Text c="dimmed" size="xs">
						{t(`training.comparisons.evaluation.status.${evaluation.status}`, evaluation.status)}
					</Text>
					{evaluation.status === "Failed" && evaluation.scoredCount < evaluation.totalCount ? (
						<Button onClick={() => onResume(evaluation.id)} size="compact-xs" variant="subtle">
							{t("training.comparisons.evaluation.resume", "Resume")}
						</Button>
					) : null}
				</Group>
			)}
		</Group>
	);
}
