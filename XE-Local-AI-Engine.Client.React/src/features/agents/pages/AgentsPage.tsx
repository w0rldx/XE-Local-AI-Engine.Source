import { Alert, Button, Group, Loader, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconDeviceFloppy, IconPlus, IconRobot, IconSparkles, IconX } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { useUnsavedChangesGuard } from "@/core/ui/hooks/useUnsavedChangesGuard";
import { toast } from "@/core/ui/notifications/Toast";
import {
	AgentDefinitionForm,
	type AgentDefinitionFormHandle,
	type AgentModelOption,
} from "@/features/agents/components/AgentDefinitionForm";
import { AgentDefinitionList } from "@/features/agents/components/AgentDefinitionList";
import { AgentExecutionLogPanel } from "@/features/agents/components/AgentExecutionLogPanel";
import { AgentTemplateGallery } from "@/features/agents/components/AgentTemplateGallery";
import { FeedbackInsightsPanel } from "@/features/agents/components/FeedbackInsightsPanel";
import { GoldenConversationPanel } from "@/features/agents/components/GoldenConversationPanel";
import { PlaybookPanel } from "@/features/agents/components/PlaybookPanel";
import { toSaveAgentDefinitionRequest } from "@/features/agents/models/AgentDefinitionMappers";
import type { AgentDefinition, AgentDefinitionFormValues } from "@/features/agents/models/AgentDefinitionModels";
import {
	deserializeOrchestrationTopology,
	emptyOrchestrationTopology,
} from "@/features/agents/models/OrchestrationTopologyModels";
import {
	useAgentDefinitions,
	useCreateAgentDefinition,
	useDeleteAgentDefinition,
	useToolCapableModels,
	useUpdateAgentDefinition,
} from "@/features/agents/queries/useAgentDefinitions";
import { useAgentManagementStore } from "@/features/agents/stores/AgentManagementStore";
import { TutorialInvitation } from "@/features/onboarding/components/TutorialInvitation";

const emptyFormValues: AgentDefinitionFormValues = {
	name: "",
	description: "",
	instructions: "",
	modelProfile: null,
	reasoningEffort: null,
	kind: "Single",
	allowedToolNames: [],
	toolApprovals: {},
	allowedSkillIds: [],
	orchestration: emptyOrchestrationTopology(),
	playbookEnabled: false,
	defaultTemporaryChat: false,
	// Extraction defaults ON (matches the backend default) so opting into memory learns from runs unless turned off.
	memoryExtractionEnabled: true,
	disableBaseScaffold: false,
	disableToolRelevanceFilter: false,
	generationMetadata: null,
};

function toFormValues(definition: AgentDefinition): AgentDefinitionFormValues {
	return {
		name: definition.name,
		description: definition.description,
		instructions: definition.instructions,
		modelProfile: definition.modelProfile,
		reasoningEffort: definition.reasoningEffort,
		kind: definition.kind,
		allowedToolNames: [...definition.allowedToolNames],
		toolApprovals: { ...definition.toolApprovals },
		allowedSkillIds: [...definition.allowedSkillIds],
		// Round-trip the persisted topology back into the editor (strips the triage from the specialist list).
		orchestration: deserializeOrchestrationTopology(definition.orchestrationTopologyJson).topology,
		playbookEnabled: definition.playbookEnabled,
		defaultTemporaryChat: definition.defaultTemporaryChat,
		memoryExtractionEnabled: definition.memoryExtractionEnabled,
		disableBaseScaffold: definition.disableBaseScaffold,
		disableToolRelevanceFilter: definition.disableToolRelevanceFilter,
		// An edit starts with no applied draft; a null block preserves whatever provenance the row already carries.
		generationMetadata: null,
	};
}

export function AgentsPage() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	const editorTarget = useAgentManagementStore((state) => state.editorTarget);
	const openCreate = useAgentManagementStore((state) => state.actions.openCreate);
	const openEdit = useAgentManagementStore((state) => state.actions.openEdit);
	const closeEditor = useAgentManagementStore((state) => state.actions.closeEditor);

	const [isGalleryOpen, setGalleryOpen] = useState(false);
	// Unsaved-edits state reported by the open editor form. Drives both the dialog close-guard and the route nav-guard.
	const [isEditorDirty, setIsEditorDirty] = useState(false);
	// Imperative handle to the editor form so the dialog footer's Save button can trigger validate-then-submit.
	const formRef = useRef<AgentDefinitionFormHandle>(null);

	// Fix the "stuck editor" bug: the management store is a module singleton whose editorTarget survives route unmount,
	// so navigating away and back would reopen the editor. Reset it when the page unmounts.
	useEffect(() => closeEditor, [closeEditor]);

	// Block in-app navigation / tab close while the editor has unsaved edits (prompts to discard via the shared confirm).
	useUnsavedChangesGuard({ isDirty: isEditorDirty });

	const definitionsQuery = useAgentDefinitions();
	const toolCapableModelsQuery = useToolCapableModels();
	const { data: modelsData } = useQuery(withResponseValidation(listLocalModelsOptions()));

	const createMutation = useCreateAgentDefinition();
	const updateMutation = useUpdateAgentDefinition();
	const deleteMutation = useDeleteAgentDefinition();

	const definitions = useMemo(() => definitionsQuery.data ?? [], [definitionsQuery.data]);
	const toolCapableModels = toolCapableModelsQuery.data ?? [];

	const modelOptions = useMemo<AgentModelOption[]>(
		() => (modelsData?.items ?? []).map((model) => ({ value: model.modelName ?? "", label: model.modelName ?? "" })),
		[modelsData],
	);

	const editingDefinition = useMemo(() => {
		if (editorTarget?.mode !== "edit") {
			return undefined;
		}
		return definitions.find((definition) => definition.id === editorTarget.id);
	}, [definitions, editorTarget]);

	const isMutating = createMutation.isPending || updateMutation.isPending || deleteMutation.isPending;
	const submitError =
		createMutation.error || updateMutation.error
			? apiErrorMessage(
					createMutation.error ?? updateMutation.error,
					t("pages.agents.errors.save", "Could not save the agent definition."),
				)
			: undefined;

	// Close the editor and drop the dirty flag together so a stale "dirty" never keeps blocking navigation after the
	// dialog is dismissed. Used for the no-confirm paths (successful save — nothing left to discard).
	const handleCloseEditor = useCallback(() => {
		setIsEditorDirty(false);
		closeEditor();
	}, [closeEditor]);

	// Single page-owned close path for every user-initiated dismiss (title-bar X, footer Cancel, overlay, escape). When
	// the form has unsaved edits it prompts to discard and only closes on confirm; otherwise it closes immediately. This
	// keeps all four dismiss affordances behaving identically (the inconsistency was: footer Cancel discarded silently
	// while the X confirmed).
	const requestCloseEditor = useCallback(async () => {
		if (isEditorDirty) {
			const confirmed = await confirm({
				title: t("components.dialogShell.unsavedTitle", "Discard unsaved changes?"),
				description: t(
					"components.dialogShell.unsavedDescription",
					"You have unsaved changes. If you leave now, they will be lost.",
				),
				confirmationText: t("common.discard", "Discard"),
				cancellationText: t("common.keepEditing", "Keep editing"),
			});
			if (!confirmed) {
				return;
			}
		}
		handleCloseEditor();
	}, [confirm, handleCloseEditor, isEditorDirty, t]);

	const handleSubmit = useCallback(
		(values: AgentDefinitionFormValues) => {
			if (editorTarget?.mode === "edit") {
				// On edit the triage (this orchestrator) is the definition's own id, pinning the topology to it.
				const request = toSaveAgentDefinitionRequest(values, editorTarget.id);
				updateMutation.mutate({ id: editorTarget.id, request }, { onSuccess: () => handleCloseEditor() });
				return;
			}

			// On create the id is unknown; the triage is assigned by the backend and re-pinned on the next edit.
			const request = toSaveAgentDefinitionRequest(values);
			createMutation.mutate(request, { onSuccess: () => handleCloseEditor() });
		},
		[createMutation, editorTarget, handleCloseEditor, updateMutation],
	);

	const handleDelete = useCallback(
		async (definition: AgentDefinition) => {
			const confirmed = await confirm({
				title: t("pages.agents.delete.title", "Delete agent"),
				description: t("pages.agents.delete.description", "Delete '{{name}}'? This cannot be undone.", {
					name: definition.name,
				}),
				confirmationText: t("common.delete", "Delete"),
				cancellationText: t("common.cancel", "Cancel"),
			});

			if (confirmed) {
				deleteMutation.mutate(definition.id, {
					onError: (error) => toast.error(apiErrorMessage(error, t("pages.agents.errors.delete", "Could not delete the agent."))),
				});
			}
		},
		[confirm, deleteMutation, t],
	);

	const isEditorOpen = editorTarget !== null;
	const formInitialValues = editingDefinition ? toFormValues(editingDefinition) : emptyFormValues;

	return (
		<PageShell>
			<TutorialInvitation tutorialId="agents-basics" />
			<PageHeader
				title={t("pages.agents.title", "Agent definitions")}
				icon={<IconRobot size={24} />}
				subtitle={t(
					"pages.agents.subtitle",
					"Author local agent personas: instructions, model, reasoning effort, and the tools they may use.",
				)}
				data-tour="agents-overview"
				actions={
					<>
						<Button
							variant="default"
							leftSection={<IconSparkles size={16} />}
							onClick={() => setGalleryOpen(true)}
							data-testid="agent-templates-button"
							data-tour="agents-templates"
						>
							{t("pages.agents.templatesButton", "Add starter agents")}
						</Button>
						<Button
							leftSection={<IconPlus size={16} />}
							onClick={openCreate}
							data-testid="agent-create-button"
							data-tour="agents-create"
						>
							{t("pages.agents.createButton", "New agent")}
						</Button>
					</>
				}
			/>

			{/* The list always renders underneath; the editor opens as a dialog on top (no more page-takeover). */}
			<SectionCard data-tour="agents-list">
				{definitionsQuery.isLoading ? (
					<Group gap="sm">
						<Loader size="sm" />
						<Text c="dimmed">{t("pages.agents.list.loading", "Loading agent definitions…")}</Text>
					</Group>
				) : null}
				{definitionsQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="agent-list-error">
						{apiErrorMessage(definitionsQuery.error, t("pages.agents.errors.load", "Could not load agent definitions."))}
					</Alert>
				) : null}
				{!definitionsQuery.isLoading && !definitionsQuery.error ? (
					<AgentDefinitionList definitions={definitions} isMutating={isMutating} onEdit={openEdit} onDelete={handleDelete} />
				) : null}
			</SectionCard>

			<DialogShell
				opened={isEditorOpen}
				onClose={requestCloseEditor}
				title={
					editorTarget?.mode === "edit"
						? t("pages.agents.editor.editTitle", "Edit agent")
						: t("pages.agents.editor.createTitle", "New agent")
				}
				// The page owns the single confirm-on-dirty path (requestCloseEditor, wired to onClose AND footer Cancel),
				// so DialogShell's built-in confirmCloseWhen stays off. Block overlay/escape dismissal while dirty so the
				// only ways out are the X and Cancel — both of which route through the same prompt.
				closeOnClickOutside={!isEditorDirty}
				closeOnEscape={!isEditorDirty}
				// Sit below the unsaved-changes confirm (ConfirmProvider uses zIndex 400) so it always renders on top.
				zIndex={300}
				footer={
					<>
						<Button
							variant="subtle"
							leftSection={<IconX size={16} />}
							onClick={requestCloseEditor}
							disabled={createMutation.isPending || updateMutation.isPending}
							data-testid="agent-form-cancel"
						>
							{t("common.cancel", "Cancel")}
						</Button>
						<Button
							leftSection={<IconDeviceFloppy size={16} />}
							onClick={() => formRef.current?.submit()}
							loading={createMutation.isPending || updateMutation.isPending}
							data-testid="agent-form-submit"
						>
							{t("common.save", "Save")}
						</Button>
					</>
				}
			>
				<Stack gap="md" data-testid="agent-editor-card">
					<AgentDefinitionForm
						key={editorTarget?.mode === "edit" ? editorTarget.id : "create"}
						ref={formRef}
						initialValues={formInitialValues}
						modelOptions={modelOptions}
						toolCapableModels={toolCapableModels}
						allDefinitions={definitions}
						selfId={editorTarget?.mode === "edit" ? editorTarget.id : ""}
						submitError={submitError}
						onSubmit={handleSubmit}
						onDirtyChange={setIsEditorDirty}
					/>
					{/* Per-agent playbook governance. Only meaningful for a persisted agent (has an id);
						    a brand-new agent must be saved first. Capability-gated under agentManagement. */}
					{editingDefinition ? (
						<PlaybookPanel
							agentDefinitionId={editingDefinition.id}
							agentName={editingDefinition.name}
							enabled={nodeCapabilities.agentManagement}
						/>
					) : null}
					{/* Per-agent read-only feedback insights. Only meaningful for a persisted
						    agent (has an id). Capability-gated under agentManagement; analytics-only, no mutations. */}
					{editingDefinition ? (
						<FeedbackInsightsPanel
							agentDefinitionId={editingDefinition.id}
							agentName={editingDefinition.name}
							enabled={nodeCapabilities.agentManagement}
						/>
					) : null}
					{/* Per-agent golden conversation set. The eval gate replays these cases against a
						    candidate action before promotion. Only meaningful for a persisted agent (has an id).
						    Capability-gated under agentManagement. */}
					{editingDefinition ? (
						<GoldenConversationPanel
							agentDefinitionId={editingDefinition.id}
							agentName={editingDefinition.name}
							enabled={nodeCapabilities.agentManagement}
						/>
					) : null}
					{/* Per-agent run diagnostics (adaptive-memory observability). Metadata-only table;
						    only meaningful for a persisted agent. Capability-gated under agentManagement. */}
					{editingDefinition ? (
						<AgentExecutionLogPanel
							agentDefinitionId={editingDefinition.id}
							agentName={editingDefinition.name}
							enabled={nodeCapabilities.agentManagement}
						/>
					) : null}
				</Stack>
			</DialogShell>

			<AgentTemplateGallery opened={isGalleryOpen} onClose={() => setGalleryOpen(false)} />
		</PageShell>
	);
}
