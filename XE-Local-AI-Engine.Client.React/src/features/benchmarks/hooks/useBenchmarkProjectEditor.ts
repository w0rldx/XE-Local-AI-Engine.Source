import { useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import type { XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkJudgePolicyDraftDto as JudgePolicyDraft } from "@/core/api/generated";
import { toast } from "@/core/ui/notifications/Toast";
import type { BenchmarkConfirmMode } from "@/features/benchmarks/components/BenchmarkConfirmationDialog";
import { benchmarkJudgeFamilyOverlap } from "@/features/benchmarks/models/BenchmarkJudgeFamily";
import type {
	BenchmarkProjectDetail,
	BenchmarkProjectDraft,
	BenchmarkRunSummary,
} from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkErrorCode } from "@/features/benchmarks/models/BenchmarkModels";
import { hasActiveJudgeAttempt, succeededRunCount } from "@/features/benchmarks/models/BenchmarkRanking";
import {
	useBenchmarkRubricPresets,
	useCreateBenchmarkProject,
	useRejudgeBenchmarkProject,
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

export type EditorMode = "create" | "edit" | null;

interface BenchmarkProjectEditorInput {
	readonly detail: BenchmarkProjectDetail | undefined;
	readonly runs: readonly BenchmarkRunSummary[];
	/** Called with the id of a project the create form just made, so the page can switch to it. */
	readonly onProjectCreated: (projectId: string) => void;
}

// Everything behind the project editor dialog and the confirmation dialog in front of it: the draft the form opens
// with, the three mutations that can save it, and the two confirmations (pairwise switch, re-judge) the node demands
// before some of those saves. It reads the selected project and its runs but owns no selection state of its own,
// which is what lets it live apart from the page controller.
export function useBenchmarkProjectEditor({ detail, runs, onProjectCreated }: BenchmarkProjectEditorInput) {
	const { t } = useTranslation();
	const [editorMode, setEditorMode] = useState<EditorMode>(null);
	const [confirmMode, setConfirmMode] = useState<BenchmarkConfirmMode>(null);
	const pendingPolicyRef = useRef<JudgePolicyDraft | null>(null);
	const pendingProjectDraftRef = useRef<BenchmarkProjectDraft | null>(null);
	// The node's own refusal of the last save, shown inside the editor. A toast would outlive the dialog and leave the
	// operator re-reading it next to fields it no longer describes.
	const [saveError, setSaveError] = useState<string | null>(null);
	// A judge from the same base family as the models it scores may prefer them. Advisory only, and dismissible per
	// project rather than by a boolean flag, so switching projects surfaces the next project's overlap again.
	const [familyWarningDismissedFor, setFamilyWarningDismissedFor] = useState<string | null>(null);
	const presetsQuery = useBenchmarkRubricPresets(editorMode !== null);
	const createProject = useCreateBenchmarkProject();
	const updateProject = useUpdateBenchmarkProject();
	const updateJudge = useUpdateBenchmarkJudgePolicy();
	const rejudgeProject = useRejudgeBenchmarkProject();

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
				onProjectCreated(project.id);
				setEditorMode(null);
			},
			onError: showSaveError,
		});
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

	return {
		editorMode,
		setEditorMode,
		confirmMode,
		setConfirmMode,
		saveError,
		setSaveError,
		editorDraft,
		presetsQuery,
		createProject,
		updateProject,
		updateJudge,
		rejudgeProject,
		saveProject,
		confirmPendingChange,
		judgeAttemptsActive,
		affectedRunCount,
		judgeFamilyOverlap,
		familyWarningDismissedFor,
		setFamilyWarningDismissedFor,
	};
}
