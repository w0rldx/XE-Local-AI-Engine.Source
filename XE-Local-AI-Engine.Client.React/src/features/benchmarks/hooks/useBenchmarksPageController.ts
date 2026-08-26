import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import type { XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkJudgePolicyDraftDto as JudgePolicyDraft } from "@/core/api/generated";
import { toast } from "@/core/ui/notifications/Toast";
import type { BenchmarkConfirmMode } from "@/features/benchmarks/components/BenchmarkConfirmationDialog";
import type { BenchmarkMatrixSelection } from "@/features/benchmarks/components/BenchmarkLaunchMatrix";
import { useBenchmarkRunSelection } from "@/features/benchmarks/hooks/useBenchmarkRunSelection";
import type { BenchmarkCell } from "@/features/benchmarks/models/BenchmarkCells";
import { benchmarkJudgeFamilyOverlap } from "@/features/benchmarks/models/BenchmarkJudgeFamily";
import type {
	BenchmarkKvCacheType,
	BenchmarkProjectDraft,
	BenchmarkRepeatMode,
	BenchmarkRunSummary,
} from "@/features/benchmarks/models/BenchmarkModels";
import {
	benchmarkBatchProgress,
	benchmarkErrorCode,
	benchmarkKvCacheTypes,
	isUnsupportedKvCacheTypeError,
} from "@/features/benchmarks/models/BenchmarkModels";
import { hasActiveJudgeAttempt, succeededRunCount } from "@/features/benchmarks/models/BenchmarkRanking";
import { benchmarkRunEstimate, medianBenchmarkRunDurationMs } from "@/features/benchmarks/models/BenchmarkRunEstimate";
import { leafBenchmarkTaskItems } from "@/features/benchmarks/models/BenchmarkTaskItems";
import type { BenchmarkBatchRejection } from "@/features/benchmarks/queries/useBenchmarks";
import {
	useBenchmarkCells,
	useBenchmarkComparisons,
	useBenchmarkProject,
	useBenchmarkProjects,
	useBenchmarkRubricPresets,
	useBenchmarkRunDetails,
	useBenchmarkRuns,
	useBenchmarkTaskItems,
	useCreateBenchmarkProject,
	useDeleteBenchmarkRun,
	useEligibleBenchmarkModels,
	useRejudgeBenchmarkProject,
	useRejudgeBenchmarkRun,
	useStartBenchmarkRun,
	useStartBenchmarkRunBatch,
	useStartBenchmarkRunFidelity,
	useUpdateBenchmarkJudgePolicy,
	useUpdateBenchmarkProject,
} from "@/features/benchmarks/queries/useBenchmarks";

const emptyProject: BenchmarkProjectDraft = {
	name: "",
	coreTask: "",
	contextTokens: 4096,
	maxOutputTokens: null,
	reasoningBudgetTokens: null,
	invocationTimeoutSeconds: null,
	agentDefinitionId: "",
	judgeEnabled: false,
	judgeMode: "pointwise",
	judgeModelName: null,
	judgeContextTokens: null,
	rubric: null,
	referenceAnswer: null,
	fidelityEnabled: false,
	fidelityKldEnabled: false,
	fidelityChunks: null,
	fidelityKldBaseModelName: null,
};

type EditorMode = "create" | "edit" | null;
const autoKvCacheType = "auto";

export interface BenchmarksPageProps {
	readonly baseModelName?: string;
	readonly tunedModelName?: string;
}

export function useBenchmarksPageController({ baseModelName, tunedModelName }: BenchmarksPageProps = {}) {
	const { t } = useTranslation();
	const projectsQuery = useBenchmarkProjects();
	const [selectedProjectId, setSelectedProjectId] = useState<string | null>(null);
	const [editorMode, setEditorMode] = useState<EditorMode>(null);
	const [confirmMode, setConfirmMode] = useState<BenchmarkConfirmMode>(null);
	const pendingPolicyRef = useRef<JudgePolicyDraft | null>(null);
	const pendingProjectDraftRef = useRef<BenchmarkProjectDraft | null>(null);
	// The node's own refusal of the last save, shown inside the editor. A toast would outlive the dialog and leave the
	// operator re-reading it next to fields it no longer describes.
	const [saveError, setSaveError] = useState<string | null>(null);
	const [selectedModel, setSelectedModel] = useState<string | null>(null);
	const [selectedKvCacheType, setSelectedKvCacheType] = useState<BenchmarkKvCacheType | typeof autoKvCacheType>(autoKvCacheType);
	const selectProject = useCallback((projectId: string | null) => {
		setSelectedProjectId(projectId);
		setSelectedKvCacheType(autoKvCacheType);
	}, []);
	const selectModel = useCallback((model: string | null) => {
		setSelectedModel(model);
		setSelectedKvCacheType(autoKvCacheType);
	}, []);
	const [repeatMode, setRepeatMode] = useState<BenchmarkRepeatMode>("Throughput");
	const [answerVarianceTemperature, setAnswerVarianceTemperature] = useState<number | null>(null);
	const [matrixOpen, setMatrixOpen] = useState(false);
	const [matrixRejections, setMatrixRejections] = useState<BenchmarkBatchRejection[]>([]);
	// The runs the last matrix launch started, kept with the project they belong to: another project's table has none
	// of them, and a progress line reading "0 of 12 done" there would be a lie rather than a stale number.
	const [batchLaunch, setBatchLaunch] = useState<{ projectId: string; runIds: string[] } | null>(null);
	const projectQuery = useBenchmarkProject(selectedProjectId);
	const runsQuery = useBenchmarkRuns(selectedProjectId);
	const modelsQuery = useEligibleBenchmarkModels(projectQuery.data?.contextTokens);
	const allModelsQuery = useEligibleBenchmarkModels();
	const presetsQuery = useBenchmarkRubricPresets(editorMode !== null);
	const createProject = useCreateBenchmarkProject();
	const updateProject = useUpdateBenchmarkProject();
	const updateJudge = useUpdateBenchmarkJudgePolicy();
	const rejudgeProject = useRejudgeBenchmarkProject();
	const rejudgeRun = useRejudgeBenchmarkRun();
	const deleteRun = useDeleteBenchmarkRun();
	const startRun = useStartBenchmarkRun();
	const startBatch = useStartBenchmarkRunBatch();
	const measureFidelity = useStartBenchmarkRunFidelity();
	const runs = useMemo(() => runsQuery.data?.items ?? [], [runsQuery.data]);
	const taskItemsQuery = useBenchmarkTaskItems(selectedProjectId);
	const leafItemCount = Math.max(leafBenchmarkTaskItems(taskItemsQuery.data?.items ?? []).length, 1);
	const medianRunMs = useMemo(() => medianBenchmarkRunDurationMs(runs), [runs]);
	const singleRunEstimate = benchmarkRunEstimate({ cellCount: 1, leafItemCount, repeatCount: 1, warmup: false }, medianRunMs);
	const isSuite = leafItemCount > 1;
	const cellsQuery = useBenchmarkCells(selectedProjectId, isSuite);
	const [ranking, setRanking] = useState<"cells" | "runs">("cells");
	const showCells = isSuite && ranking === "cells";
	const { selectedRunIds, selectRun, toggleRun } = useBenchmarkRunSelection(runs, baseModelName, tunedModelName);
	// Already in cache for the compare view and the live panes; read here for the frozen reasoning budget, which the
	// list projection does not carry.
	const selectedRunDetails = useBenchmarkRunDetails(selectedRunIds);
	const [chartsOpen, setChartsOpen] = useState(false);
	const batchProgress = useMemo(
		() => (batchLaunch && batchLaunch.projectId === selectedProjectId ? benchmarkBatchProgress(runs, batchLaunch.runIds) : null),
		[batchLaunch, selectedProjectId, runs],
	);

	useEffect(() => {
		if (!selectedProjectId && projectsQuery.data?.[0]) {
			selectProject(projectsQuery.data[0].id);
		}
		if (selectedProjectId && projectsQuery.data && !projectsQuery.data.some((project) => project.id === selectedProjectId)) {
			selectProject(projectsQuery.data[0]?.id ?? null);
		}
	}, [projectsQuery.data, selectedProjectId, selectProject]);
	// The pick belongs to one prospective run; a different model or project is a different run, so it falls back to Auto
	// rather than silently carrying a quantized type onto a model that may not support it.
	const detail = projectQuery.data;
	const editDraft = useMemo<BenchmarkProjectDraft>(
		() =>
			detail
				? {
						name: detail.name,
						coreTask: detail.coreTask,
						contextTokens: detail.contextTokens,
						maxOutputTokens: detail.maxOutputTokens,
						reasoningBudgetTokens: detail.reasoningBudgetTokens,
						invocationTimeoutSeconds: detail.invocationTimeoutSeconds,
						agentDefinitionId: detail.agentDefinitionId,
						judgeEnabled: detail.judge.enabled,
						judgeMode: detail.judge.mode,
						judgeModelName: detail.judge.modelName,
						judgeContextTokens: detail.judge.requestedContextTokens,
						rubric: detail.judge.rubric,
						referenceAnswer: detail.judge.referenceAnswer,
						fidelityEnabled: detail.fidelity.enabled,
						fidelityKldEnabled: detail.fidelity.kldEnabled,
						fidelityChunks: detail.fidelity.chunks,
						fidelityKldBaseModelName: detail.fidelity.kldBaseModelName,
					}
				: emptyProject,
		[detail],
	);
	const editorDraft = editorMode === "edit" ? editDraft : emptyProject;
	const judgeAttemptsActive = hasActiveJudgeAttempt(runs);
	const affectedRunCount = succeededRunCount(runs);
	// A judge from the same base family as the models it scores may prefer them. Advisory only, and dismissible per
	// project rather than by a boolean flag, so switching projects surfaces the next project's overlap again.
	const [familyWarningDismissedFor, setFamilyWarningDismissedFor] = useState<string | null>(null);
	const judgeFamilyOverlap = useMemo(
		() =>
			detail?.judge.enabled === true
				? benchmarkJudgeFamilyOverlap(
						detail.judge.modelName,
						runs.map((run) => run.primaryModelName),
					)
				: null,
		[detail, runs],
	);

	// The node re-checks every rule with numbers the form cannot see, and answers with a machine-readable `code` beside
	// its sentence. Both are shown: the sentence is what the operator acts on, the code is what they can quote.
	const showSaveError = (error: unknown): void => {
		const message = apiErrorMessage(error, t("pages.benchmarks.errors.projectSave", "Could not save the project."));
		const code = benchmarkErrorCode(error);
		setSaveError(code === null ? message : `${message} (${code})`);
	};

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
					pendingPolicyRef.current = null;
					setSaveError(null);
				},
				onError: (error) => {
					const code = benchmarkErrorCode(error);
					if (code === "RejudgeRequired") {
						pendingPolicyRef.current = policy;
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
					showSaveError(error);
				},
			},
		);
	};

	const saveProject = (draft: BenchmarkProjectDraft): void => {
		setSaveError(null);
		// Switching a project INTO pairwise commits its queue to a quadratic number of judge calls, so it is confirmed
		// against the node's own estimate rather than saved on the click that selected the mode.
		if (
			editorMode === "edit" &&
			detail &&
			draft.judgeEnabled &&
			draft.judgeMode === "pairwise" &&
			detail.judge.mode !== "pairwise"
		) {
			pendingProjectDraftRef.current = draft;
			setConfirmMode("pairwise");
			return;
		}
		if (editorMode === "edit" && detail?.isFrozen) {
			saveJudgePolicy(
				draft.judgeEnabled
					? {
							modelName: draft.judgeModelName ?? "",
							contextTokens: draft.judgeContextTokens ?? 0,
							mode: draft.judgeMode,
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
					onError: showSaveError,
				},
			);
			return;
		}
		createProject.mutate(draft, {
			onSuccess: (project) => {
				selectProject(project.id);
				setEditorMode(null);
			},
			onError: showSaveError,
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
	const confirmPendingChange = (): void => {
		if (confirmMode === "pairwise") {
			const draft = pendingProjectDraftRef.current;
			setConfirmMode(null);
			pendingProjectDraftRef.current = null;
			if (draft && detail) {
				saveJudgePolicy(
					{
						modelName: draft.judgeModelName ?? "",
						contextTokens: draft.judgeContextTokens ?? 0,
						mode: draft.judgeMode,
						rubric: draft.rubric,
						referenceAnswer: draft.referenceAnswer,
					},
					true,
				);
			}
			return;
		}
		if (confirmMode === "judgePolicy") {
			saveJudgePolicy(pendingPolicyRef.current, true);
		} else {
			rejudgeAll();
		}
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
	// Only read while the project actually judges pairwise; shares the query key the matrix below uses, so the two are
	// one request. A fit that is not current yields no intervals — a stale band is worse than none.
	const isPairwise = projectQuery.data?.judge.mode === "pairwise";
	const comparisonsQuery = useBenchmarkComparisons(selectedProjectId, isPairwise);
	const pairwiseScores = useMemo(() => {
		const fit = comparisonsQuery.data?.fit;
		return fit?.isCurrent ? new Map(fit.scores.map((score) => [score.runId, score])) : undefined;
	}, [comparisonsQuery.data]);

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
		runsQuery,
		runs,
		pairwiseScores,
		selectedRunIds,
		rejudgeRun,
		deleteRun,
		measureFidelity,
		toggleRun,
		selectRun,
		rejudgeOne,
		measureRunFidelity,
		removeRun,
		selectedRunDetails,
		chartsOpen,
		setChartsOpen,
		editorMode,
		saveError,
		setSaveError,
		editorDraft,
		presetsQuery,
		createProject,
		updateProject,
		updateJudge,
		saveProject,
		matrixOpen,
		matrixRejections,
		startBatch,
		startMatrix,
		taskItemsQuery,
		leafItemCount,
		medianRunMs,
		singleRunEstimate,
		isSuite,
		cellsQuery,
		ranking,
		setRanking,
		showCells,
		rerunCell,
		confirmMode,
		confirmPendingChange,
		autoKvCacheType,
		benchmarkKvCacheTypes,
		startRunErrorMessage,
	};
}

export type BenchmarksPageController = ReturnType<typeof useBenchmarksPageController>;
