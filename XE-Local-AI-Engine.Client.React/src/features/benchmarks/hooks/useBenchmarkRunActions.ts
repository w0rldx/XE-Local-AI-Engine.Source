import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { toast } from "@/core/ui/notifications/Toast";
import type { BenchmarkCell } from "@/features/benchmarks/models/BenchmarkCells";
import type {
	BenchmarkKvCacheType,
	BenchmarkMatrixSelection,
	BenchmarkProjectDetail,
	BenchmarkRepeatMode,
	BenchmarkRunSummary,
} from "@/features/benchmarks/models/BenchmarkModels";
import {
	benchmarkBatchProgress,
	benchmarkKvCacheTypes,
	isUnsupportedKvCacheTypeError,
} from "@/features/benchmarks/models/BenchmarkModels";
import type { BenchmarkBatchRejection } from "@/features/benchmarks/queries/useBenchmarks";
import {
	useDeleteBenchmarkRun,
	useEligibleBenchmarkModels,
	useRejudgeBenchmarkRun,
	useStartBenchmarkRun,
	useStartBenchmarkRunBatch,
	useStartBenchmarkRunFidelity,
} from "@/features/benchmarks/queries/useBenchmarks";

const autoKvCacheType = "auto";

interface BenchmarkRunActionsInput {
	readonly detail: BenchmarkProjectDetail | undefined;
	readonly selectedProjectId: string | null;
	readonly runs: readonly BenchmarkRunSummary[];
	/** Focuses a run in the compare/live panes; every launch points the operator at what it just started. */
	readonly selectRun: (runId: string) => void;
}

// The launch configuration an operator picks for a prospective run (model, KV-cache type, repeat mode) together with
// every mutation that acts on runs: start one, start a matrix, re-run a cell, re-judge, measure fidelity, delete.
// The configuration and the launches are one concern — a launch reads the picks directly — so they share a hook.
export function useBenchmarkRunActions({ detail, selectedProjectId, runs, selectRun }: BenchmarkRunActionsInput) {
	const { t } = useTranslation();
	const [selectedModel, setSelectedModel] = useState<string | null>(null);
	const [selectedKvCacheType, setSelectedKvCacheType] = useState<BenchmarkKvCacheType | typeof autoKvCacheType>(autoKvCacheType);
	const [repeatMode, setRepeatMode] = useState<BenchmarkRepeatMode>("Throughput");
	const [answerVarianceTemperature, setAnswerVarianceTemperature] = useState<number | null>(null);
	const [matrixOpen, setMatrixOpen] = useState(false);
	const [matrixRejections, setMatrixRejections] = useState<BenchmarkBatchRejection[]>([]);
	// The runs the last matrix launch started, kept with the project they belong to: another project's table has none
	// of them, and a progress line reading "0 of 12 done" there would be a lie rather than a stale number.
	const [batchLaunch, setBatchLaunch] = useState<{ projectId: string; runIds: string[] } | null>(null);
	const modelsQuery = useEligibleBenchmarkModels(detail?.contextTokens);
	const allModelsQuery = useEligibleBenchmarkModels();
	const rejudgeRun = useRejudgeBenchmarkRun();
	const deleteRun = useDeleteBenchmarkRun();
	const startRun = useStartBenchmarkRun();
	const startBatch = useStartBenchmarkRunBatch();
	const measureFidelity = useStartBenchmarkRunFidelity();

	// The pick belongs to one prospective run; a different model or project is a different run, so it falls back to Auto
	// rather than silently carrying a quantized type onto a model that may not support it. The page calls
	// `resetKvCacheType` from its own project-selection callback, which keeps the reset on the click rather than
	// deferring it to an effect that would render once with the stale type.
	const resetKvCacheType = useCallback(() => setSelectedKvCacheType(autoKvCacheType), []);
	const selectModel = useCallback((model: string | null) => {
		setSelectedModel(model);
		setSelectedKvCacheType(autoKvCacheType);
	}, []);

	const batchProgress = useMemo(
		() => (batchLaunch && batchLaunch.projectId === selectedProjectId ? benchmarkBatchProgress(runs, batchLaunch.runIds) : null),
		[batchLaunch, selectedProjectId, runs],
	);

	// A 422 is the node refusing this KV type for this runtime (quantized KV on CPU, or a binary whose manifest does not
	// support it). Its sanitized reason is the useful half; the hint says what actually gets the run started. A local
	// response-validation failure reuses 422 as its status, and telling the operator to pick f16 would be nonsense there.
	const startRunErrorMessage = (error: unknown): string => {
		const message = apiErrorMessage(error, t("pages.benchmarks.errors.start", "Could not start the benchmark run."));
		return isUnsupportedKvCacheTypeError(error)
			? `${message} ${t("pages.benchmarks.errors.kvUnsupportedHint", "Pick f16 explicitly to run this model on this runtime.")}`
			: message;
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

	const measureRunFidelity = (run: BenchmarkRunSummary): void => {
		measureFidelity.mutate(run, {
			onError: (error) =>
				toast.error(
					apiErrorMessage(error, t("pages.benchmarks.errors.measureFidelity", "Could not queue a fidelity measurement.")),
				),
		});
	};

	// One request for the whole matrix. The node answers per cell, so a refused combination is reported in the dialog
	// beside the ones that started rather than failing everything the operator picked.
	const startMatrix = (selection: BenchmarkMatrixSelection): void => {
		if (!detail) {
			return;
		}
		startBatch.mutate(
			{ projectId: detail.id, expectedProjectVersion: detail.version, ...selection },
			{
				onSuccess: (result) => {
					setMatrixRejections(result.rejected);
					setBatchLaunch(result.startedRunIds.length > 0 ? { projectId: detail.id, runIds: result.startedRunIds } : null);
					if (result.startedRunIds[0]) {
						selectRun(result.startedRunIds[0]);
					}
					if (result.rejected.length === 0) {
						setMatrixOpen(false);
					}
				},
				onError: (error) => toast.error(startRunErrorMessage(error)),
			},
		);
	};

	const rerunCell = (cell: BenchmarkCell): void => {
		if (!detail) {
			return;
		}
		startRun.mutate(
			{
				projectId: detail.id,
				modelName: cell.primaryModelName,
				expectedProjectVersion: detail.version,
				kvCacheType: benchmarkKvCacheTypes.find((type) => type === cell.kvCacheType) ?? null,
				repeatMode,
				answerVarianceTemperature: repeatMode === "AnswerVariance" ? answerVarianceTemperature : null,
			},
			{
				onSuccess: (run) => selectRun(run.id),
				onError: (error) => toast.error(startRunErrorMessage(error)),
			},
		);
	};

	const removeRun = (run: BenchmarkRunSummary): void => {
		deleteRun.mutate(run, {
			onError: (error) =>
				toast.error(apiErrorMessage(error, t("pages.benchmarks.errors.delete", "Could not delete this terminal run."))),
		});
	};

	return {
		selectedModel,
		selectModel,
		modelsQuery,
		allModelsQuery,
		selectedKvCacheType,
		setSelectedKvCacheType,
		resetKvCacheType,
		autoKvCacheType,
		benchmarkKvCacheTypes,
		repeatMode,
		setRepeatMode,
		answerVarianceTemperature,
		setAnswerVarianceTemperature,
		matrixOpen,
		setMatrixOpen,
		matrixRejections,
		setMatrixRejections,
		batchProgress,
		setBatchLaunch,
		startRun,
		startBatch,
		measureFidelity,
		deleteRun,
		rejudgeRun,
		startRunErrorMessage,
		startMatrix,
		rerunCell,
		rejudgeOne,
		measureRunFidelity,
		removeRun,
	};
}
