import {
	ActionIcon,
	Alert,
	Badge,
	Button,
	Collapse,
	Group,
	Loader,
	NumberInput,
	Paper,
	Select,
	Stack,
	Switch,
	Text,
	Textarea,
	TextInput,
	Tooltip,
} from "@mantine/core";
import {
	IconAlertTriangle,
	IconArrowDown,
	IconArrowRight,
	IconArrowUp,
	IconCheck,
	IconChevronDown,
	IconChevronUp,
	IconFlag,
	IconFlask,
	IconPencil,
	IconPlus,
	IconSparkles,
	IconTrash,
	IconX,
} from "@tabler/icons-react";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { PromoteConflictError } from "@/features/agents/api/PlaybookActionsApi";
import {
	comparePlaybookActions,
	emptyPlaybookActionForm,
	type EvalResult,
	type PlaybookAction,
	type PlaybookActionFormValues,
	playbookActionFormSchema,
	toPlaybookActionFormValues,
	toSavePlaybookActionRequest,
	toSaveSuggestedActionRequest,
} from "@/features/agents/models/PlaybookActionModels";
import type { PlaybookMonitorItem, PlaybookMonitorStatus } from "@/features/agents/models/PlaybookMonitorModels";
import {
	useAnalyzePlaybook,
	useCreatePlaybookAction,
	useDeletePlaybookAction,
	usePlaybookActions,
	usePromoteSuggestedAction,
	useRejectSuggestedAction,
	useRunEval,
	useUpdatePlaybookAction,
	useUpdateSuggestedAction,
} from "@/features/agents/queries/usePlaybookActions";
import { usePlaybookMonitor } from "@/features/agents/queries/usePlaybookMonitor";

interface PlaybookPanelProps {
	// The agent whose playbook is managed. The panel is rendered by the parent only when agentManagement is on; it
	// also guards internally so it can never render its surface when the capability is off.
	agentDefinitionId: string;
	agentName: string;
	// FE-static capability gate (folded under agentManagement). When false the panel renders nothing.
	enabled: boolean;
}

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

// Editor target: "create" a new action or "edit" an existing one by id. null = editor closed.
type EditorTarget = { mode: "create" } | { mode: "edit"; id: string } | null;

// Per-agent playbook governance panel (Playbook P1). Lists the agent's playbook actions in injection order
// (ascending Priority), shows each action's provenance (P1 renders source "Manual"), and offers a per-action
// enable/disable toggle plus add/edit/delete and reorder-by-priority. Capability-gated under agentManagement —
// when `enabled` is false it renders nothing.
export function PlaybookPanel({ agentDefinitionId, agentName, enabled }: PlaybookPanelProps) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	const [editorTarget, setEditorTarget] = useState<EditorTarget>(null);

	const actionsQuery = usePlaybookActions(enabled ? agentDefinitionId : null);
	// Playbook P5 — read-only cohort-monitoring signals per Enabled action + the relevance-retrieval config. Joined
	// to the rows below by actionId; the read is independent of the action list (its own loading/error path) so a
	// monitor failure degrades to "no signal" rather than blanking the governance panel.
	const monitorQuery = usePlaybookMonitor(enabled ? agentDefinitionId : null);
	const createMutation = useCreatePlaybookAction(agentDefinitionId);
	const updateMutation = useUpdatePlaybookAction(agentDefinitionId);
	const updateSuggestedMutation = useUpdateSuggestedAction(agentDefinitionId);
	const deleteMutation = useDeletePlaybookAction(agentDefinitionId);
	const analyzeMutation = useAnalyzePlaybook(agentDefinitionId);
	const promoteMutation = usePromoteSuggestedAction(agentDefinitionId);
	const rejectMutation = useRejectSuggestedAction(agentDefinitionId);
	const runEvalMutation = useRunEval(agentDefinitionId);

	// Manual governance: the existing Enabled/Disabled actions (and any unknown state that degraded to Disabled).
	// Suggested actions are the analysis-proposed proposals awaiting human review and render in their own section.
	const orderedActions = useMemo(
		() =>
			[...(actionsQuery.data ?? [])].filter((action) => action.state !== "Suggested").sort(comparePlaybookActions),
		[actionsQuery.data],
	);

	const suggestedActions = useMemo(
		() =>
			[...(actionsQuery.data ?? [])].filter((action) => action.state === "Suggested").sort(comparePlaybookActions),
		[actionsQuery.data],
	);

	const editingAction = useMemo(() => {
		if (editorTarget?.mode !== "edit") {
			return undefined;
		}
		// Edit can target a manual action or a Suggested proposal (operators may tweak a proposal before approving).
		return (
			orderedActions.find((action) => action.id === editorTarget.id) ??
			suggestedActions.find((action) => action.id === editorTarget.id)
		);
	}, [editorTarget, orderedActions, suggestedActions]);

	// Next priority for a brand-new action: one past the current max so it sorts at the end of the list.
	const nextPriority = useMemo(
		() => orderedActions.reduce((max, action) => Math.max(max, action.priority), -1) + 1,
		[orderedActions],
	);

	// Playbook P5 — the number of currently Enabled actions (the cohort under monitoring + the count the relevance
	// gate / cap indicator reason about). Suggested/Disabled/Archived are excluded.
	const enabledCount = useMemo(
		() => orderedActions.filter((action) => action.state === "Enabled").length,
		[orderedActions],
	);

	// Playbook P5 — index the monitoring signals by actionId so each Enabled row can join its signal in O(1). An
	// action with no monitor item (no enable clock yet, or the read failed) simply renders the neutral "no signal".
	const monitorByActionId = useMemo(() => {
		const map = new Map<string, PlaybookMonitorItem>();
		for (const item of monitorQuery.data?.items ?? []) {
			map.set(item.actionId, item);
		}
		return map;
	}, [monitorQuery.data]);

	// Playbook P5 — the relevance-retrieval config. When more actions are Enabled than the threshold, injection is
	// gated to the top-K most relevant per turn; the banner below surfaces that with the live numbers.
	const retrieval = monitorQuery.data?.retrieval ?? null;
	const showRelevanceBanner = retrieval !== null && enabledCount > retrieval.threshold;

	const isMutating =
		createMutation.isPending ||
		updateMutation.isPending ||
		updateSuggestedMutation.isPending ||
		deleteMutation.isPending ||
		analyzeMutation.isPending ||
		promoteMutation.isPending ||
		rejectMutation.isPending ||
		runEvalMutation.isPending;

	const closeEditor = useCallback(() => setEditorTarget(null), []);

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
				description: t(
					"pages.agents.playbook.delete.description",
					"Delete this playbook action? This cannot be undone.",
				),
				confirmationText: t("common.delete", "Delete"),
				cancellationText: t("common.cancel", "Cancel"),
			});

			if (confirmed) {
				deleteMutation.mutate(action.id);
			}
		},
		[confirm, deleteMutation, t],
	);

	// Run the analysis agent. The mutation result + invalidation refresh the Suggested section; the empty-result
	// notice is derived from analyzeMutation below.
	const handleAnalyze = useCallback(() => {
		analyzeMutation.mutate();
	}, [analyzeMutation]);

	const handlePromote = useCallback(
		(action: PlaybookAction) => {
			promoteMutation.mutate(action.id);
		},
		[promoteMutation],
	);

	// Playbook P4 — run the eval gate for a single Suggested action against the agent's golden set. The mutation
	// invalidation refreshes the row's eval badge + the Approve gate (Approve stays disabled until passed).
	const handleRunEval = useCallback(
		(action: PlaybookAction) => {
			runEvalMutation.mutate(action.id);
		},
		[runEvalMutation],
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
				rejectMutation.mutate(action.id);
			}
		},
		[confirm, rejectMutation, t],
	);

	if (!enabled) {
		return null;
	}

	const isEditorOpen = editorTarget !== null;
	const formInitialValues = editingAction
		? toPlaybookActionFormValues(editingAction)
		: emptyPlaybookActionForm(nextPriority);
	const isEditingSuggested = editingAction?.state === "Suggested";
	const saveError = createMutation.error ?? updateMutation.error ?? updateSuggestedMutation.error;
	const submitError = saveError
		? errorMessage(saveError, t("pages.agents.playbook.errors.save", "Could not save the playbook action."))
		: undefined;

	// "No new suggestions" notice: shown only after a completed analyze run that returned zero proposals and when
	// there are no Suggested actions outstanding to review.
	const showNoSuggestionsNotice =
		analyzeMutation.isSuccess && analyzeMutation.data.length === 0 && suggestedActions.length === 0;
	// A blocked promote (the eval gate, HTTP 409) surfaces as a typed PromoteConflictError carrying the precise
	// reason (needs eval / regressed / stale). Prefer a localized message keyed by its status; fall back to the
	// generic review error for any other promote/reject failure.
	const reviewError = promoteMutation.error ?? rejectMutation.error ?? runEvalMutation.error;
	const promoteRejectError =
		promoteMutation.error instanceof PromoteConflictError
			? t(
					`pages.agents.playbook.eval.conflict.${promoteMutation.error.status}`,
					promoteMutation.error.message,
				)
			: reviewError
				? errorMessage(reviewError, t("pages.agents.playbook.errors.review", "Could not update the suggestion."))
				: undefined;

	return (
		<Paper withBorder={true} radius="md" p="md" data-testid={`playbook-panel-${agentDefinitionId}`}>
			<Stack gap="sm">
				<Group justify="space-between" align="flex-start">
					<Stack gap={2}>
						<Text fw={600}>{t("pages.agents.playbook.title", "Operating playbook")}</Text>
						<Text size="xs" c="dimmed">
							{t(
								"pages.agents.playbook.subtitle",
								"Manual playbook actions appended to {{name}}'s instructions when its playbook is enabled.",
								{ name: agentName },
							)}
						</Text>
					</Stack>
					{!isEditorOpen ? (
						<Group gap="xs" wrap="nowrap">
							<Button
								size="xs"
								variant="default"
								leftSection={<IconSparkles size={14} />}
								onClick={handleAnalyze}
								loading={analyzeMutation.isPending}
								disabled={isMutating}
								data-testid="playbook-analyze-button"
							>
								{t("pages.agents.playbook.analyzeButton", "Analyze feedback")}
							</Button>
							<Button
								size="xs"
								variant="light"
								leftSection={<IconPlus size={14} />}
								onClick={() => setEditorTarget({ mode: "create" })}
								disabled={isMutating}
								data-testid="playbook-add-button"
							>
								{t("pages.agents.playbook.addButton", "Add action")}
							</Button>
						</Group>
					) : null}
				</Group>

				{/* Playbook P5 — bounded-store cap indicator: how many actions are currently Enabled for this agent. The
				    hard cap (MaxEnabledActions) is server-owned and not in this response, so the panel shows only the live
				    Enabled count; the cap is surfaced via the typed CapReached 409 reason when a promote is blocked. */}
				<Group gap="xs" align="center" data-testid="playbook-cap-indicator">
					<Text size="xs" c="dimmed">
						{t("pages.agents.playbook.monitor.enabledCount", "{{count}} actions enabled", {
							count: enabledCount,
						})}
					</Text>
				</Group>

				{/* Playbook P5 — relevance-gated banner: once more actions are Enabled than the retrieval threshold,
				    only the top-K most relevant are injected per turn (not all of them). Rendered only in that regime so
				    the operator knows not every Enabled action reaches the model on every turn. */}
				{showRelevanceBanner && retrieval !== null ? (
					<Alert color="blue" variant="light" data-testid="playbook-relevance-banner">
						{t(
							"pages.agents.playbook.monitor.relevanceBanner",
							"Injection is relevance-gated: top-{{topK}} of {{count}} actions per turn.",
							{ topK: retrieval.topK, count: enabledCount },
						)}
					</Alert>
				) : null}

				{analyzeMutation.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="playbook-analyze-error">
						{errorMessage(
							analyzeMutation.error,
							t("pages.agents.playbook.errors.analyze", "Could not analyze feedback."),
						)}
					</Alert>
				) : null}

				{promoteRejectError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="playbook-review-error">
						{promoteRejectError}
					</Alert>
				) : null}

				{showNoSuggestionsNotice ? (
					<Text size="sm" c="dimmed" data-testid="playbook-no-suggestions">
						{t("pages.agents.playbook.noSuggestions", "No new suggestions from the latest analysis.")}
					</Text>
				) : null}

				{suggestedActions.length > 0 ? (
					<Stack gap={6} data-testid="playbook-suggested-section">
						<Text size="xs" fw={600} c="dimmed">
							{t("pages.agents.playbook.suggestedHeading", "Suggested by analysis")}
						</Text>
						{suggestedActions.map((action) => (
							<SuggestedActionRow
								key={action.id}
								action={action}
								disabled={isMutating}
								isEvaluating={runEvalMutation.isPending && runEvalMutation.variables === action.id}
								onApprove={() => handlePromote(action)}
								onEdit={() => setEditorTarget({ mode: "edit", id: action.id })}
								onReject={() => handleReject(action)}
								onRunEval={() => handleRunEval(action)}
							/>
						))}
					</Stack>
				) : null}

				{deleteMutation.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="playbook-delete-error">
						{errorMessage(
							deleteMutation.error,
							t("pages.agents.playbook.errors.delete", "Could not delete the playbook action."),
						)}
					</Alert>
				) : null}

				{isEditorOpen ? (
					<PlaybookActionForm
						key={editorTarget?.mode === "edit" ? editorTarget.id : "create"}
						initialValues={formInitialValues}
						hideStateField={isEditingSuggested}
						isSubmitting={
							createMutation.isPending || updateMutation.isPending || updateSuggestedMutation.isPending
						}
						submitError={submitError}
						onSubmit={handleSubmit}
						onCancel={closeEditor}
					/>
				) : null}

				{actionsQuery.isLoading ? (
					<Group gap="sm" data-testid="playbook-loading">
						<Loader size="sm" />
						<Text c="dimmed" size="sm">
							{t("pages.agents.playbook.loading", "Loading playbook…")}
						</Text>
					</Group>
				) : null}

				{actionsQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="playbook-list-error">
						{errorMessage(actionsQuery.error, t("pages.agents.playbook.errors.load", "Could not load the playbook."))}
					</Alert>
				) : null}

				{!actionsQuery.isLoading && !actionsQuery.error && orderedActions.length === 0 && !isEditorOpen ? (
					<Text size="sm" c="dimmed" data-testid="playbook-empty">
						{t("pages.agents.playbook.empty", "No playbook actions yet.")}
					</Text>
				) : null}

				{orderedActions.map((action, index) => {
					const isEnabled = action.state === "Enabled";
					// Playbook P5 — the cohort-monitoring signal for an Enabled action (joined by id). Disabled actions
					// carry no live signal; an Enabled action with no monitor item yet (no enable clock) renders the
					// neutral placeholder inside MonitorSignal.
					const monitorItem = isEnabled ? (monitorByActionId.get(action.id) ?? null) : null;
					return (
						<Paper withBorder={true} p="xs" key={action.id} data-testid={`playbook-action-${action.id}`}>
							<Stack gap={6}>
								<Group justify="space-between" align="flex-start" wrap="nowrap">
									<Stack gap={4} style={{ flex: 1, minWidth: 0 }}>
										<Group gap="xs" align="center" wrap="wrap">
											<Badge size="xs" variant="light" color="grape" data-testid={`playbook-source-${action.id}`}>
												{t(`pages.agents.playbook.source.${action.source}`, action.source)}
											</Badge>
											{action.scope ? (
												<Badge size="xs" variant="outline" color="gray">
													{action.scope}
												</Badge>
											) : null}
											<Text size="xs" c="dimmed">
												{t("pages.agents.playbook.priorityLabel", "Priority {{priority}}", {
													priority: action.priority,
												})}
											</Text>
										</Group>
										<Text size="sm">{action.behavior}</Text>
										{action.triggerCondition ? (
											<Text size="xs" c="dimmed">
												{t("pages.agents.playbook.triggerLabel", "When: {{trigger}}", {
													trigger: action.triggerCondition,
												})}
											</Text>
										) : null}
									</Stack>
									<Group gap={4} wrap="nowrap">
										<ActionIcon
											aria-label={t("pages.agents.playbook.moveUpAria", "Move up")}
											variant="subtle"
											size="sm"
											disabled={isMutating || index === 0}
											onClick={() => handleMove(index, "up")}
											data-testid={`playbook-move-up-${action.id}`}
										>
											<IconArrowUp size={14} />
										</ActionIcon>
										<ActionIcon
											aria-label={t("pages.agents.playbook.moveDownAria", "Move down")}
											variant="subtle"
											size="sm"
											disabled={isMutating || index === orderedActions.length - 1}
											onClick={() => handleMove(index, "down")}
											data-testid={`playbook-move-down-${action.id}`}
										>
											<IconArrowDown size={14} />
										</ActionIcon>
										<ActionIcon
											aria-label={t("pages.agents.playbook.editAria", "Edit action")}
											variant="subtle"
											size="sm"
											disabled={isMutating}
											onClick={() => setEditorTarget({ mode: "edit", id: action.id })}
											data-testid={`playbook-edit-${action.id}`}
										>
											<IconPencil size={14} />
										</ActionIcon>
										<ActionIcon
											aria-label={t("pages.agents.playbook.deleteAria", "Delete action")}
											variant="subtle"
											color="red"
											size="sm"
											disabled={isMutating}
											onClick={() => handleDelete(action)}
											data-testid={`playbook-delete-${action.id}`}
										>
											<IconTrash size={14} />
										</ActionIcon>
									</Group>
								</Group>
								<Switch
									size="sm"
									checked={isEnabled}
									disabled={isMutating}
									label={
										<Badge size="xs" variant="light" color={isEnabled ? "teal" : "gray"}>
											{isEnabled
												? t("pages.agents.playbook.state.enabled", "enabled")
												: t("pages.agents.playbook.state.disabled", "disabled")}
										</Badge>
									}
									onChange={(event) => handleToggleState(action, event.currentTarget.checked)}
									data-testid={`playbook-toggle-${action.id}`}
								/>
								{isEnabled ? <MonitorSignal actionId={action.id} item={monitorItem} /> : null}
							</Stack>
						</Paper>
					);
				})}
			</Stack>
		</Paper>
	);
}

// Render an analysis confidence fraction (0..1) as a whole-percent string for display.
function toConfidencePercent(confidence: number): string {
	return `${Math.round(confidence * 100)}%`;
}

// Playbook P5 — render a down-vote rate fraction (0..1) as a whole-percent string for the before→after signal.
function toDownRatePercent(rate: number): string {
	return `${Math.round(rate * 100)}%`;
}

// Playbook P5 — the Mantine badge color per monitor verdict. Improved is positive (teal), Regressed negative
// (red), Flat/InsufficientData neutral (gray) so the signal reads at a glance.
const monitorStatusColors: Record<PlaybookMonitorStatus, string> = {
	Improved: "teal",
	Regressed: "red",
	Flat: "gray",
	InsufficientData: "gray",
};

// English fallback copy per verdict (the i18n key carries the localized text). "InsufficientData" reads as a
// short human phrase rather than the wire token.
const monitorStatusFallbacks: Record<PlaybookMonitorStatus, string> = {
	Improved: "Improved",
	Regressed: "Regressed",
	Flat: "Flat",
	InsufficientData: "Insufficient data",
};

interface MonitorSignalProps {
	actionId: string;
	// The monitoring signal for this Enabled action, or null when there is no signal yet (no enable clock / read
	// failed) — in which case a neutral "—" placeholder renders.
	item: PlaybookMonitorItem | null;
}

// Playbook P5 — the cohort-monitoring signal for one Enabled action: a status badge (Improved / Flat / Regressed /
// Insufficient data), a compact before→after down-rate (e.g. "12% → 5%"), and a flag marker for operator review
// when the action is flagged (dead/harmful — coarse, agent-level signal; the operator decides, never auto-disabled).
// An action with no monitor item renders a neutral placeholder so the row reads consistently.
function MonitorSignal({ actionId, item }: MonitorSignalProps) {
	const { t } = useTranslation();

	if (item === null) {
		return (
			<Text size="xs" c="dimmed" data-testid={`playbook-monitor-none-${actionId}`}>
				{t("pages.agents.playbook.monitor.none", "—")}
			</Text>
		);
	}

	return (
		<Group gap="xs" align="center" wrap="wrap" data-testid={`playbook-monitor-${actionId}`}>
			<Badge
				size="xs"
				variant="light"
				color={monitorStatusColors[item.status]}
				data-testid={`playbook-monitor-status-${actionId}`}
			>
				{t(`pages.agents.playbook.monitor.status.${item.status}`, monitorStatusFallbacks[item.status])}
			</Badge>
			<Group gap={4} align="center" wrap="nowrap" data-testid={`playbook-monitor-rate-${actionId}`}>
				<Text size="xs" c="dimmed">
					{toDownRatePercent(item.beforeDownRate)}
				</Text>
				<IconArrowRight size={12} aria-hidden={true} />
				<Text size="xs" c="dimmed">
					{toDownRatePercent(item.afterDownRate)}
				</Text>
				<Text size="xs" c="dimmed">
					{t("pages.agents.playbook.monitor.downRateLabel", "down-vote rate")}
				</Text>
			</Group>
			{item.facetToolName ? (
				<Badge size="xs" variant="outline" color="gray" data-testid={`playbook-monitor-facet-${actionId}`}>
					{item.facetToolName}
				</Badge>
			) : null}
			{item.flagged ? (
				<Badge
					size="xs"
					variant="filled"
					color="orange"
					leftSection={<IconFlag size={10} />}
					data-testid={`playbook-monitor-flag-${actionId}`}
				>
					{t("pages.agents.playbook.monitor.flag", "Needs review")}
				</Badge>
			) : null}
		</Group>
	);
}

interface SuggestedActionRowProps {
	action: PlaybookAction;
	disabled: boolean;
	// True while THIS row's eval is in flight (drives the Run-eval button's loading spinner).
	isEvaluating: boolean;
	onApprove: () => void;
	onEdit: () => void;
	onReject: () => void;
	onRunEval: () => void;
}

// Playbook P4 — why the Approve/Promote control is gated, derived from the row's evalResult. Drives both the
// disabled state and the tooltip copy: no eval has run, the eval is stale (ran against an older version), the
// candidate regressed a prior-good case, or the gate is satisfied (passed).
type PromoteGateReason = "needsEval" | "stale" | "regressed" | "passed";

function promoteGateReason(action: PlaybookAction): PromoteGateReason {
	const result = action.evalResult;
	if (result === null) {
		return "needsEval";
	}
	if (result.actionVersionAtEval !== action.version) {
		return "stale";
	}
	return result.passed ? "passed" : "regressed";
}

// English fallback copy for the gated-Approve tooltip (the i18n key carries the localized text). Keyed by gate
// reason; "passed" maps to empty since the tooltip is suppressed when the gate is satisfied.
const gateReasonFallbacks: Record<PromoteGateReason, string> = {
	needsEval: "Run the eval before approving this suggestion.",
	stale: "This suggestion changed since the last eval. Re-run the eval before approving.",
	regressed: "The eval regressed a prior-good case. Resolve the regression before approving.",
	passed: "",
};

function gateReasonFallback(reason: PromoteGateReason): string {
	return gateReasonFallbacks[reason];
}

// One analysis-proposed (Suggested) action awaiting human review (Playbook P3). Surfaces the provenance
// ("Analysis"), the analysis confidence as a percent, the proposed behavior, and an evidence affordance: a
// "Based on N feedback items" summary that expands to the cited ids and points the operator to the feedback
// insights panel mounted on the same page. Carries Approve (→ promote), Edit (→ existing edit form/PUT), and
// Reject (→ archive) controls.
function SuggestedActionRow({
	action,
	disabled,
	isEvaluating,
	onApprove,
	onEdit,
	onReject,
	onRunEval,
}: SuggestedActionRowProps) {
	const { t } = useTranslation();
	const [evidenceOpen, setEvidenceOpen] = useState(false);

	const feedbackIds = action.sourceFeedbackIds ?? [];
	const evidenceCount = feedbackIds.length;

	// Playbook P4 — the eval gate. Approve is disabled until the latest eval passed against the action's current
	// version; the tooltip explains why (no eval yet / regressed / stale).
	const gateReason = promoteGateReason(action);
	const canPromote = gateReason === "passed";
	const promoteTooltip = canPromote
		? null
		: t(`pages.agents.playbook.eval.gate.${gateReason}`, gateReasonFallback(gateReason));

	return (
		<Paper withBorder={true} p="xs" key={action.id} data-testid={`playbook-suggested-${action.id}`}>
			<Stack gap={6}>
				<Group justify="space-between" align="flex-start" wrap="nowrap">
					<Stack gap={4} style={{ flex: 1, minWidth: 0 }}>
						<Group gap="xs" align="center" wrap="wrap">
							<Badge size="xs" variant="light" color="grape" data-testid={`playbook-suggested-source-${action.id}`}>
								{t("pages.agents.playbook.source.Analysis", "Analysis")}
							</Badge>
							{action.confidence !== null ? (
								<Badge
									size="xs"
									variant="outline"
									color="blue"
									data-testid={`playbook-suggested-confidence-${action.id}`}
								>
									{t("pages.agents.playbook.confidenceLabel", "Confidence {{value}}", {
										value: toConfidencePercent(action.confidence),
									})}
								</Badge>
							) : null}
						</Group>
						<Text size="sm">{action.behavior}</Text>
						{action.triggerCondition ? (
							<Text size="xs" c="dimmed">
								{t("pages.agents.playbook.triggerLabel", "When: {{trigger}}", {
									trigger: action.triggerCondition,
								})}
							</Text>
						) : null}
						<Stack gap={2}>
							{evidenceCount > 0 ? (
								<Button
									size="compact-xs"
									variant="subtle"
									color="gray"
									leftSection={evidenceOpen ? <IconChevronUp size={12} /> : <IconChevronDown size={12} />}
									onClick={() => setEvidenceOpen((open) => !open)}
									data-testid={`playbook-suggested-evidence-toggle-${action.id}`}
								>
									{t("pages.agents.playbook.evidenceSummary", "Based on {{count}} feedback items", {
										count: evidenceCount,
									})}
								</Button>
							) : (
								<Text size="xs" c="dimmed" data-testid={`playbook-suggested-evidence-empty-${action.id}`}>
									{t("pages.agents.playbook.evidenceEmpty", "No cited feedback items.")}
								</Text>
							)}
							{evidenceCount > 0 ? (
								<Collapse expanded={evidenceOpen}>
									<Stack gap={2} data-testid={`playbook-suggested-evidence-${action.id}`}>
										<Text size="xs" c="dimmed">
											{t(
												"pages.agents.playbook.evidenceHint",
												"Review these items in the Feedback insights panel below.",
											)}
										</Text>
										{feedbackIds.map((feedbackId) => (
											<Text key={feedbackId} size="xs" c="dimmed" style={{ wordBreak: "break-all" }}>
												{feedbackId}
											</Text>
										))}
									</Stack>
								</Collapse>
							) : null}
							<EvalResultSummary actionId={action.id} evalResult={action.evalResult} />
						</Stack>
					</Stack>
					<Group gap={4} wrap="nowrap">
						<ActionIcon
							aria-label={t("pages.agents.playbook.editAria", "Edit action")}
							variant="subtle"
							size="sm"
							disabled={disabled}
							onClick={onEdit}
							data-testid={`playbook-suggested-edit-${action.id}`}
						>
							<IconPencil size={14} />
						</ActionIcon>
					</Group>
				</Group>
				<Group gap="xs">
					<Button
						size="xs"
						variant="default"
						leftSection={<IconFlask size={14} />}
						loading={isEvaluating}
						disabled={disabled}
						onClick={onRunEval}
						data-testid={`playbook-suggested-run-eval-${action.id}`}
					>
						{t("pages.agents.playbook.eval.runButton", "Run eval")}
					</Button>
					{/* The Approve/Promote control is eval-gated: disabled until the latest eval passed for the action's
					    current version. A Tooltip explains why when the gate blocks. Wrap the button so the tooltip still
					    shows for a disabled control. */}
					<Tooltip
						label={promoteTooltip ?? ""}
						disabled={canPromote}
						withArrow={true}
						data-testid={`playbook-suggested-approve-tooltip-${action.id}`}
					>
						<Button
							size="xs"
							variant="light"
							color="teal"
							leftSection={<IconCheck size={14} />}
							disabled={disabled || !canPromote}
							onClick={onApprove}
							data-testid={`playbook-suggested-approve-${action.id}`}
						>
							{t("pages.agents.playbook.approveButton", "Approve")}
						</Button>
					</Tooltip>
					<Button
						size="xs"
						variant="subtle"
						color="red"
						leftSection={<IconX size={14} />}
						disabled={disabled}
						onClick={onReject}
						data-testid={`playbook-suggested-reject-${action.id}`}
					>
						{t("pages.agents.playbook.rejectButton", "Reject")}
					</Button>
				</Group>
			</Stack>
		</Paper>
	);
}

interface EvalResultSummaryProps {
	actionId: string;
	evalResult: EvalResult | null;
}

// Playbook P4 — render the eval-gate outcome for a Suggested action: a pass/fail badge with the
// regressed/golden case counts and an expandable list of the regressed cases (goldenCaseId + how it was scored).
// Renders nothing until an eval has run (evalResult null) — the gated Approve tooltip already explains "run eval".
function EvalResultSummary({ actionId, evalResult }: EvalResultSummaryProps) {
	const { t } = useTranslation();
	const [open, setOpen] = useState(false);

	if (evalResult === null) {
		return null;
	}

	const regressedCases = evalResult.cases.filter((evalCase) => evalCase.regressed);

	return (
		<Stack gap={2} data-testid={`playbook-suggested-eval-${actionId}`}>
			<Group gap="xs" align="center" wrap="wrap">
				<Badge
					size="xs"
					variant="light"
					color={evalResult.passed ? "teal" : "red"}
					data-testid={`playbook-suggested-eval-status-${actionId}`}
				>
					{evalResult.passed
						? t("pages.agents.playbook.eval.passed", "Eval passed")
						: t("pages.agents.playbook.eval.failed", "Eval failed")}
				</Badge>
				<Text size="xs" c="dimmed" data-testid={`playbook-suggested-eval-counts-${actionId}`}>
					{t("pages.agents.playbook.eval.counts", "{{regressed}} regressed / {{golden}} golden cases", {
						regressed: evalResult.regressedCaseCount,
						golden: evalResult.goldenCaseCount,
					})}
				</Text>
			</Group>
			{evalResult.goldenCaseTotal > evalResult.goldenCaseCount ? (
				<Text size="xs" c="dimmed" data-testid={`playbook-suggested-eval-truncated-${actionId}`}>
					{t("pages.agents.playbook.eval.truncated", "Evaluated {{evaluated}} of {{total}} golden cases.", {
						evaluated: evalResult.goldenCaseCount,
						total: evalResult.goldenCaseTotal,
					})}
				</Text>
			) : null}
			{regressedCases.length > 0 ? (
				<Stack gap={2}>
					<Button
						size="compact-xs"
						variant="subtle"
						color="gray"
						leftSection={open ? <IconChevronUp size={12} /> : <IconChevronDown size={12} />}
						onClick={() => setOpen((current) => !current)}
						data-testid={`playbook-suggested-eval-toggle-${actionId}`}
					>
						{t("pages.agents.playbook.eval.regressedToggle", "Show {{count}} regressed cases", {
							count: regressedCases.length,
						})}
					</Button>
					<Collapse expanded={open}>
						<Stack gap={2} data-testid={`playbook-suggested-eval-regressed-${actionId}`}>
							{regressedCases.map((evalCase) => (
								<Text
									key={evalCase.goldenCaseId}
									size="xs"
									c="dimmed"
									style={{ wordBreak: "break-all" }}
								>
									{t("pages.agents.playbook.eval.regressedCase", "{{id}} (scored by {{scoredBy}})", {
										id: evalCase.goldenCaseId,
										scoredBy: t(`pages.agents.playbook.eval.scoredBy.${evalCase.scoredBy}`, evalCase.scoredBy),
									})}
								</Text>
							))}
						</Stack>
					</Collapse>
				</Stack>
			) : null}
		</Stack>
	);
}

interface PlaybookActionFormProps {
	initialValues: PlaybookActionFormValues;
	// Hide the Enabled/Disabled state Select when editing a Suggested action — it stays Suggested until Approve, so
	// the operator never sets its state from this form.
	hideStateField?: boolean;
	isSubmitting: boolean;
	submitError?: string;
	onSubmit: (values: PlaybookActionFormValues) => void;
	onCancel: () => void;
}

// Inline add/edit form for a single playbook action. Controlled Mantine inputs validated with the shared Zod
// schema on submit; mirrors AgentDefinitionForm's local-state + on-submit-validate pattern.
function PlaybookActionForm({
	initialValues,
	hideStateField = false,
	isSubmitting,
	submitError,
	onSubmit,
	onCancel,
}: PlaybookActionFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState<PlaybookActionFormValues>(initialValues);
	const [fieldErrors, setFieldErrors] = useState<Partial<Record<keyof PlaybookActionFormValues, string>>>({});

	const handleSubmit = useCallback(() => {
		const result = playbookActionFormSchema.safeParse(values);
		if (!result.success) {
			const nextErrors: Partial<Record<keyof PlaybookActionFormValues, string>> = {};
			for (const issue of result.error.issues) {
				const key = issue.path[0];
				if (typeof key === "string") {
					nextErrors[key as keyof PlaybookActionFormValues] = issue.message;
				}
			}
			setFieldErrors(nextErrors);
			return;
		}

		setFieldErrors({});
		onSubmit(values);
	}, [onSubmit, values]);

	return (
		<Paper withBorder={true} p="sm" data-testid="playbook-action-form">
			<Stack gap="sm">
				<Textarea
					label={t("pages.agents.playbook.form.behavior.label", "Behavior")}
					description={t(
						"pages.agents.playbook.form.behavior.description",
						"Instruction text appended to the agent's system prompt.",
					)}
					placeholder={t("pages.agents.playbook.form.behavior.placeholder", "Always cite your sources…")}
					value={values.behavior}
					required={true}
					autosize={true}
					minRows={2}
					error={
						fieldErrors.behavior
							? t("pages.agents.playbook.form.behavior.required", "Behavior is required")
							: undefined
					}
					onChange={(event) => setValues((current) => ({ ...current, behavior: event.currentTarget.value }))}
					data-testid="playbook-form-behavior"
				/>
				<Group grow={true} align="flex-start">
					<TextInput
						label={t("pages.agents.playbook.form.scope.label", "Scope")}
						placeholder={t("pages.agents.playbook.form.scope.placeholder", "Optional topic/tool tag")}
						value={values.scope}
						onChange={(event) => setValues((current) => ({ ...current, scope: event.currentTarget.value }))}
						data-testid="playbook-form-scope"
					/>
					<NumberInput
						label={t("pages.agents.playbook.form.priority.label", "Priority")}
						description={t("pages.agents.playbook.form.priority.description", "Lower numbers are injected first.")}
						value={values.priority}
						allowDecimal={false}
						onChange={(value) =>
							setValues((current) => ({
								...current,
								priority: typeof value === "number" ? value : Number.parseInt(`${value}`, 10) || 0,
							}))
						}
						data-testid="playbook-form-priority"
					/>
					{hideStateField ? null : (
						<Select
							label={t("pages.agents.playbook.form.state.label", "State")}
							data={[
								{ value: "Enabled", label: t("pages.agents.playbook.state.enabled", "enabled") },
								{ value: "Disabled", label: t("pages.agents.playbook.state.disabled", "disabled") },
							]}
							value={values.state}
							allowDeselect={false}
							onChange={(value) =>
								setValues((current) => ({ ...current, state: value === "Disabled" ? "Disabled" : "Enabled" }))
							}
							data-testid="playbook-form-state"
						/>
					)}
				</Group>
				<Textarea
					label={t("pages.agents.playbook.form.triggerCondition.label", "Trigger condition")}
					description={t(
						"pages.agents.playbook.form.triggerCondition.description",
						"Optional advisory note describing when this applies (display-only in this phase).",
					)}
					placeholder={t("pages.agents.playbook.form.triggerCondition.placeholder", "When the user asks for…")}
					value={values.triggerCondition}
					autosize={true}
					minRows={1}
					onChange={(event) => setValues((current) => ({ ...current, triggerCondition: event.currentTarget.value }))}
					data-testid="playbook-form-trigger"
				/>
				{submitError ? (
					<Alert color="red" data-testid="playbook-form-submit-error">
						{submitError}
					</Alert>
				) : null}
				<Group justify="flex-end">
					<Button
						variant="subtle"
						size="xs"
						leftSection={<IconX size={14} />}
						onClick={onCancel}
						disabled={isSubmitting}
						data-testid="playbook-form-cancel"
					>
						{t("common.cancel", "Cancel")}
					</Button>
					<Button
						size="xs"
						onClick={handleSubmit}
						loading={isSubmitting}
						data-testid="playbook-form-submit"
					>
						{t("common.save", "Save")}
					</Button>
				</Group>
			</Stack>
		</Paper>
	);
}
