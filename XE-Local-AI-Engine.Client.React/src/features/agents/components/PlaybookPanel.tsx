import {
	ActionIcon,
	Alert,
	Badge,
	Button,
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
} from "@mantine/core";
import { IconAlertTriangle, IconArrowDown, IconArrowUp, IconPencil, IconPlus, IconTrash, IconX } from "@tabler/icons-react";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { useConfirm } from "@/core/ui/hooks/useConfirm";
import {
	comparePlaybookActions,
	emptyPlaybookActionForm,
	type PlaybookAction,
	type PlaybookActionFormValues,
	playbookActionFormSchema,
	toPlaybookActionFormValues,
	toSavePlaybookActionRequest,
} from "@/features/agents/models/PlaybookActionModels";
import {
	useCreatePlaybookAction,
	useDeletePlaybookAction,
	usePlaybookActions,
	useUpdatePlaybookAction,
} from "@/features/agents/queries/usePlaybookActions";

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
	const createMutation = useCreatePlaybookAction(agentDefinitionId);
	const updateMutation = useUpdatePlaybookAction(agentDefinitionId);
	const deleteMutation = useDeletePlaybookAction(agentDefinitionId);

	const orderedActions = useMemo(
		() => [...(actionsQuery.data ?? [])].sort(comparePlaybookActions),
		[actionsQuery.data],
	);

	const editingAction = useMemo(() => {
		if (editorTarget?.mode !== "edit") {
			return undefined;
		}
		return orderedActions.find((action) => action.id === editorTarget.id);
	}, [editorTarget, orderedActions]);

	// Next priority for a brand-new action: one past the current max so it sorts at the end of the list.
	const nextPriority = useMemo(
		() => orderedActions.reduce((max, action) => Math.max(max, action.priority), -1) + 1,
		[orderedActions],
	);

	const isMutating = createMutation.isPending || updateMutation.isPending || deleteMutation.isPending;

	const closeEditor = useCallback(() => setEditorTarget(null), []);

	const handleSubmit = useCallback(
		(values: PlaybookActionFormValues) => {
			const request = toSavePlaybookActionRequest(values);
			if (editorTarget?.mode === "edit") {
				updateMutation.mutate({ actionId: editorTarget.id, request }, { onSuccess: closeEditor });
				return;
			}
			createMutation.mutate(request, { onSuccess: closeEditor });
		},
		[closeEditor, createMutation, editorTarget, updateMutation],
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

	if (!enabled) {
		return null;
	}

	const isEditorOpen = editorTarget !== null;
	const formInitialValues = editingAction
		? toPlaybookActionFormValues(editingAction)
		: emptyPlaybookActionForm(nextPriority);
	const submitError =
		createMutation.error || updateMutation.error
			? errorMessage(
					createMutation.error ?? updateMutation.error,
					t("pages.agents.playbook.errors.save", "Could not save the playbook action."),
				)
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
					) : null}
				</Group>

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
						isSubmitting={createMutation.isPending || updateMutation.isPending}
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
							</Stack>
						</Paper>
					);
				})}
			</Stack>
		</Paper>
	);
}

interface PlaybookActionFormProps {
	initialValues: PlaybookActionFormValues;
	isSubmitting: boolean;
	submitError?: string;
	onSubmit: (values: PlaybookActionFormValues) => void;
	onCancel: () => void;
}

// Inline add/edit form for a single playbook action. Controlled Mantine inputs validated with the shared Zod
// schema on submit; mirrors AgentDefinitionForm's local-state + on-submit-validate pattern.
function PlaybookActionForm({ initialValues, isSubmitting, submitError, onSubmit, onCancel }: PlaybookActionFormProps) {
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
