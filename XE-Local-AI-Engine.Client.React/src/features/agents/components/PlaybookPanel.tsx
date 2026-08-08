import {
	Alert,
	Button,
	Group,
	Loader,
	Paper,
	SegmentedControl,
	Stack,
	Text,
} from "@mantine/core";
import {
	IconAlertTriangle,
	IconPlus,
	IconSparkles,
} from "@tabler/icons-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	MEMORY_SCOPES,
	memoryScopeFallbacks,
} from "@/features/agents/components/PlaybookActionDisplay";
import { PlaybookActionForm } from "@/features/agents/components/PlaybookActionForm";
import { PlaybookActionRow } from "@/features/agents/components/PlaybookActionRow";
import { usePlaybookPanelHandlers } from "@/features/agents/components/PlaybookPanelHandlers";
import { SuggestedActionRow } from "@/features/agents/components/PlaybookSuggestedActionRow";
import {
	comparePlaybookActions,
	emptyPlaybookActionForm,
	type MemoryScope,
	toPlaybookActionFormValues,
} from "@/features/agents/models/PlaybookActionModels";
import type { PlaybookMonitorItem } from "@/features/agents/models/PlaybookMonitorModels";
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

// Editor target: "create" a new action or "edit" an existing one by id. null = editor closed.
type EditorTarget = { mode: "create" } | { mode: "edit"; id: string } | null;

// The scope-filter value: "all" lists every scope; otherwise a single MemoryScope filtered server-side via `?scope=`.
type ScopeFilter = "all" | MemoryScope;

// Per-agent playbook governance panel. Lists the agent's playbook actions in injection order
// (ascending Priority), shows each action's provenance (e.g. "Manual" for hand-authored actions), and offers a per-action
// enable/disable toggle plus add/edit/delete and reorder-by-priority. Capability-gated under agentManagement —
// when `enabled` is false it renders nothing.
export function PlaybookPanel({ agentDefinitionId, agentName, enabled }: PlaybookPanelProps) {
	const { t } = useTranslation();

	const [editorTarget, setEditorTarget] = useState<EditorTarget>(null);
	// Adaptive-memory scope filter. "all" lists every scope; a single scope rides the server `?scope=` param so the
	// list is filtered at the source (the Suggested/manual split below still applies to the filtered set).
	const [scopeFilter, setScopeFilter] = useState<ScopeFilter>("all");

	const actionsQuery = usePlaybookActions(enabled ? agentDefinitionId : null, scopeFilter === "all" ? null : scopeFilter);
	// Read-only cohort-monitoring signals per Enabled action + the relevance-retrieval config. Joined
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
		() => [...(actionsQuery.data ?? [])].filter((action) => action.state !== "Suggested").sort(comparePlaybookActions),
		[actionsQuery.data],
	);

	const suggestedActions = useMemo(
		() => [...(actionsQuery.data ?? [])].filter((action) => action.state === "Suggested").sort(comparePlaybookActions),
		[actionsQuery.data],
	);

	const editingAction = useMemo(() => {
		if (editorTarget?.mode !== "edit") { return undefined; }
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

	// The number of currently Enabled actions (the cohort under monitoring + the count the relevance
	// gate / cap indicator reason about). Suggested/Disabled/Archived are excluded.
	const enabledCount = useMemo(
		() => orderedActions.filter((action) => action.state === "Enabled").length,
		[orderedActions],
	);

	// Index the monitoring signals by actionId so each Enabled row can join its signal in O(1). An
	// action with no monitor item (no enable clock yet, or the read failed) simply renders the neutral "no signal".
	const monitorByActionId = useMemo(() => {
		const map = new Map<string, PlaybookMonitorItem>();
		for (const item of monitorQuery.data?.items ?? []) {
			map.set(item.actionId, item);
		}
		return map;
	}, [monitorQuery.data]);

	// The relevance-retrieval config. When more actions are Enabled than the threshold, injection is
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

	const closeEditor = () => setEditorTarget(null);

	const {
		handleSubmit,
		handleToggleState,
		handleMove,
		handleDelete,
		handleAnalyze,
		handlePromote,
		handleRunEval,
		handleReject,
	} = usePlaybookPanelHandlers({
		agentDefinitionId,
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
	});

	if (!enabled) { return null; }

	const isEditorOpen = editorTarget !== null;
	const formInitialValues = editingAction ? toPlaybookActionFormValues(editingAction) : emptyPlaybookActionForm(nextPriority);
	const isEditingSuggested = editingAction?.state === "Suggested";
	const saveError = createMutation.error ?? updateMutation.error ?? updateSuggestedMutation.error;
	const submitError = saveError
		? apiErrorMessage(saveError, t("pages.agents.playbook.errors.save", "Could not save the playbook action."))
		: undefined;

	// "No new suggestions" notice: shown only after a completed analyze run that returned zero proposals and when
	// there are no Suggested actions outstanding to review.
	const showNoSuggestionsNotice =
		analyzeMutation.isSuccess && analyzeMutation.data.length === 0 && suggestedActions.length === 0;

	return (
		<Paper withBorder={true} radius="md" p="md" data-testid={`playbook-panel-${agentDefinitionId}`}>
			<Stack gap="sm">
				<Group justify="space-between" align="flex-start">
					<Stack gap={2}>
						<Text fw={600}>{t("pages.agents.playbook.title", "Adaptive memory")}</Text>
						<Text size="xs" c="dimmed">
							{t(
								"pages.agents.playbook.subtitle",
								"Playbook actions — manual, analysis-proposed, or learned from runs — appended to {{name}}'s instructions when its memory is enabled.",
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

				{/* Bounded-store cap indicator: how many actions are currently Enabled for this agent. The
				    hard cap (MaxEnabledActions) is server-owned and not in this response, so the panel shows only the live
				    Enabled count; the cap is surfaced via the typed CapReached 409 reason when a promote is blocked. */}
				<Group gap="xs" align="center" data-testid="playbook-cap-indicator">
					<Text size="xs" c="dimmed">
						{t("pages.agents.playbook.monitor.enabledCount", "{{count}} actions enabled", { count: enabledCount })}
					</Text>
				</Group>

				{/* Adaptive-memory scope filter. "All" lists every scope; selecting a single scope filters the list at
				    the source (server `?scope=`). Failure surfaces negative-guidance ("don't do X") memories distinctly. */}
				<SegmentedControl
					size="xs"
					value={scopeFilter}
					onChange={(value) => setScopeFilter(value as ScopeFilter)}
					data={[
						{ value: "all", label: t("pages.agents.playbook.scopeFilter.all", "All") },
						...MEMORY_SCOPES.map((scope) => ({
							value: scope,
							label: t(`pages.agents.playbook.scope.${scope}`, memoryScopeFallbacks[scope]),
						})),
					]}
					data-testid="playbook-scope-filter"
				/>

				{/* Relevance-gated banner: once more actions are Enabled than the retrieval threshold,
				    only the top-K most relevant are injected per turn. */}
				{showRelevanceBanner && retrieval !== null ? (
					<Alert color="blue" variant="light" data-testid="playbook-relevance-banner">
						{t(
							"pages.agents.playbook.monitor.relevanceBanner",
							"Injection is relevance-gated: top-{{topK}} of {{count}} actions per turn.",
							{ topK: retrieval.topK, count: enabledCount },
						)}{" "}
						<Text span={true} size="sm" data-testid="playbook-relevance-ranker">
							{retrieval.ranker === "embedding"
								? t("pages.agents.playbook.monitor.rankerEmbedding", "Ranked by embedding similarity (model {{model}}).", {
										model: retrieval.embeddingModel ?? "",
									})
								: t("pages.agents.playbook.monitor.rankerLexical", "Ranked by lexical overlap.")}
						</Text>
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

				{isEditorOpen ? (
					<PlaybookActionForm
						key={editorTarget?.mode === "edit" ? editorTarget.id : "create"}
						initialValues={formInitialValues}
						hideStateField={isEditingSuggested}
						isSubmitting={createMutation.isPending || updateMutation.isPending || updateSuggestedMutation.isPending}
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
						{apiErrorMessage(actionsQuery.error, t("pages.agents.playbook.errors.load", "Could not load the playbook."))}
					</Alert>
				) : null}

				{!actionsQuery.isLoading && !actionsQuery.error && orderedActions.length === 0 && !isEditorOpen ? (
					<Text size="sm" c="dimmed" data-testid="playbook-empty">
						{t("pages.agents.playbook.empty", "No playbook actions yet.")}
					</Text>
				) : null}

				{orderedActions.map((action, index) => (
					<PlaybookActionRow
						key={action.id}
						action={action}
						index={index}
						isFirst={index === 0}
						isLast={index === orderedActions.length - 1}
						isMutating={isMutating}
						// The cohort-monitoring signal for an Enabled action (joined by id). Disabled actions carry no live
						// signal; an Enabled action with no monitor item yet (no enable clock) renders the neutral placeholder.
						monitorItem={action.state === "Enabled" ? (monitorByActionId.get(action.id) ?? null) : null}
						onMove={handleMove}
						onEdit={(id) => setEditorTarget({ mode: "edit", id })}
						onDelete={handleDelete}
						onToggleState={handleToggleState}
					/>
				))}
			</Stack>
		</Paper>
	);
}
