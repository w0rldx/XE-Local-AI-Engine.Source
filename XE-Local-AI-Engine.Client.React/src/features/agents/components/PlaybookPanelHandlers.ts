import { useCallback } from "react";
import { useTranslation } from "react-i18next";

import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import {
	PromoteConflictError,
	toSavePlaybookActionRequest,
	toSaveSuggestedActionRequest,
} from "@/features/agents/models/PlaybookActionMappers";
import {
	type PlaybookAction,
	type PlaybookActionFormValues,
	toPlaybookActionFormValues,
} from "@/features/agents/models/PlaybookActionModels";

// Shape of the mutation handles this hook depends on. Using the minimum needed surface so
// the hook stays decoupled from the full mutation-object shape.
interface MutationHandle<TArgs> {
	mutate: (args: TArgs, options?: { onSuccess?: () => void; onError?: (error: unknown) => void }) => void;
	isPending?: boolean;
}

interface UsePlaybookPanelHandlersArgs {
	agentDefinitionId: string;
	orderedActions: PlaybookAction[];
	editingAction: PlaybookAction | undefined;
	editorTarget: { mode: "create" } | { mode: "edit"; id: string } | null;
	closeEditor: () => void;
	createMutation: MutationHandle<PlaybookActionFormValues extends infer T ? T : never> & {
		mutate: (req: ReturnType<typeof toSavePlaybookActionRequest>, opts?: { onSuccess?: () => void }) => void;
	};
	updateMutation: {
		mutate: (
			args: { actionId: string; request: ReturnType<typeof toSavePlaybookActionRequest> },
			opts?: { onSuccess?: () => void },
		) => void;
	};
	updateSuggestedMutation: {
		mutate: (
			args: { actionId: string; request: ReturnType<typeof toSaveSuggestedActionRequest> },
			opts?: { onSuccess?: () => void },
		) => void;
	};
	deleteMutation: { mutate: (id: string, opts?: { onError?: (error: unknown) => void }) => void };
	analyzeMutation: { mutate: (arg: undefined, opts?: { onError?: (error: unknown) => void }) => void };
	promoteMutation: { mutate: (id: string, opts?: { onError?: (error: unknown) => void }) => void };
	rejectMutation: { mutate: (id: string, opts?: { onError?: (error: unknown) => void }) => void };
	runEvalMutation: { mutate: (id: string, opts?: { onError?: (error: unknown) => void }) => void };
}

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

// All event-handler callbacks for PlaybookPanel. Extracted here so the component body stays
// focused on state wiring and render; the mutation orchestration logic lives here.
export function usePlaybookPanelHandlers({
	orderedActions,
	editingAction,
	editorTarget,
	closeEditor,
	createMutation,
	updateMutation,
	updateSuggestedMutation,
	deleteMutation,
	analyzeMutation,
	promoteMutation,
	rejectMutation,
	runEvalMutation,
}: UsePlaybookPanelHandlersArgs) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	// A blocked promote (the eval gate, HTTP 409) surfaces as a typed PromoteConflictError carrying the precise
	// reason (needs eval / regressed / stale). Prefer a localized message keyed by its status; fall back to the
	// generic review error for any other promote/reject/run-eval failure.
	const reviewErrorMessage = useCallback(
		(error: unknown): string =>
			error instanceof PromoteConflictError
				? t(`pages.agents.playbook.eval.conflict.${error.status}`, error.message)
				: errorMessage(error, t("pages.agents.playbook.errors.review", "Could not update the suggestion.")),
		[t],
	);

	const handleSubmit = useCallback(
		(values: PlaybookActionFormValues) => {
			if (editorTarget?.mode === "edit") {
				// A Suggested (Analysis-provenance) action must edit via the dedicated `/suggested` route — the manual
				// PUT 404s on it. The body omits `state`; the action stays Suggested until Approve.
				if (editingAction?.state === "Suggested") {
					updateSuggestedMutation.mutate(
						{ actionId: editorTarget.id, request: toSaveSuggestedActionRequest(values) },
						{ onSuccess: closeEditor },
					);
					return;
				}
				updateMutation.mutate(
					{ actionId: editorTarget.id, request: toSavePlaybookActionRequest(values) },
					{ onSuccess: closeEditor },
				);
				return;
			}
			createMutation.mutate(toSavePlaybookActionRequest(values), { onSuccess: closeEditor });
		},
		[closeEditor, createMutation, editingAction, editorTarget, updateMutation, updateSuggestedMutation],
	);

	// Toggle a single action's enable state in place without opening the editor. Reuses the action's existing
	// fields so the toggle never drops the behavior/priority/scope.
	const handleToggleState = useCallback(
		(action: PlaybookAction, nextEnabled: boolean) => {
			const request = toSavePlaybookActionRequest({
				...toPlaybookActionFormValues(action),
				state: nextEnabled ? "Enabled" : "Disabled",
			});
			updateMutation.mutate({ actionId: action.id, request });
		},
		[updateMutation],
	);

	// Reorder by swapping the priority of an action with its neighbor in display order. Persisted via update so
	// the new injection order survives a reload.
	const handleMove = useCallback(
		(index: number, direction: "up" | "down") => {
			const targetIndex = direction === "up" ? index - 1 : index + 1;
			const current = orderedActions[index];
			const neighbor = orderedActions[targetIndex];
			if (!current || !neighbor) {
				return;
			}
			updateMutation.mutate({
				actionId: current.id,
				request: toSavePlaybookActionRequest({ ...toPlaybookActionFormValues(current), priority: neighbor.priority }),
			});
			updateMutation.mutate({
				actionId: neighbor.id,
				request: toSavePlaybookActionRequest({ ...toPlaybookActionFormValues(neighbor), priority: current.priority }),
			});
		},
		[orderedActions, updateMutation],
	);

	const handleDelete = useCallback(
		async (action: PlaybookAction) => {
			const confirmed = await confirm({
				title: t("pages.agents.playbook.delete.title", "Delete playbook action"),
				description: t("pages.agents.playbook.delete.description", "Delete this playbook action? This cannot be undone."),
				confirmationText: t("common.delete", "Delete"),
				cancellationText: t("common.cancel", "Cancel"),
			});
			if (confirmed) {
				deleteMutation.mutate(action.id, {
					onError: (error) =>
						toast.error(errorMessage(error, t("pages.agents.playbook.errors.delete", "Could not delete the playbook action."))),
				});
			}
		},
		[confirm, deleteMutation, t],
	);

	// Run the analysis agent. The mutation result + invalidation refresh the Suggested section; the empty-result
	// notice is derived from analyzeMutation below.
	const handleAnalyze = useCallback(() => {
		analyzeMutation.mutate(undefined, {
			onError: (error) => toast.error(errorMessage(error, t("pages.agents.playbook.errors.analyze", "Could not analyze feedback."))),
		});
	}, [analyzeMutation, t]);

	const handlePromote = useCallback(
		(action: PlaybookAction) => {
			promoteMutation.mutate(action.id, { onError: (error) => toast.error(reviewErrorMessage(error)) });
		},
		[promoteMutation, reviewErrorMessage],
	);

	// Run the eval gate for a single Suggested action against the agent's golden set. The mutation
	// invalidation refreshes the row's eval badge + the Approve gate (Approve stays disabled until passed).
	const handleRunEval = useCallback(
		(action: PlaybookAction) => {
			runEvalMutation.mutate(action.id, { onError: (error) => toast.error(reviewErrorMessage(error)) });
		},
		[runEvalMutation, reviewErrorMessage],
	);

	const handleReject = useCallback(
		async (action: PlaybookAction) => {
			const confirmed = await confirm({
				title: t("pages.agents.playbook.reject.title", "Reject suggestion"),
				description: t(
					"pages.agents.playbook.reject.description",
					"Reject this suggested action? It will be archived and not injected.",
				),
				confirmationText: t("pages.agents.playbook.reject.confirm", "Reject"),
				cancellationText: t("common.cancel", "Cancel"),
			});
			if (confirmed) {
				rejectMutation.mutate(action.id, { onError: (error) => toast.error(reviewErrorMessage(error)) });
			}
		},
		[confirm, rejectMutation, reviewErrorMessage, t],
	);

	return {
		handleSubmit,
		handleToggleState,
		handleMove,
		handleDelete,
		handleAnalyze,
		handlePromote,
		handleRunEval,
		handleReject,
	};
}
