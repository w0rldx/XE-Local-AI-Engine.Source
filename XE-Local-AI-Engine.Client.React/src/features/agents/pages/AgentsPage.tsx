import { Button, Group, Loader, Stack, Text } from "@mantine/core";
import { IconDeviceFloppy, IconPlus, IconRobot, IconSparkles, IconX } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { AgentDefinitionForm, type AgentModelOption } from "@/features/agents/components/AgentDefinitionForm";
import { AgentDefinitionList } from "@/features/agents/components/AgentDefinitionList";
import { AgentExecutionLogPanel } from "@/features/agents/components/AgentExecutionLogPanel";
import { AgentTemplateGallery } from "@/features/agents/components/AgentTemplateGallery";
import { FeedbackInsightsPanel } from "@/features/agents/components/FeedbackInsightsPanel";
import { GoldenConversationPanel } from "@/features/agents/components/GoldenConversationPanel";
import { PlaybookPanel } from "@/features/agents/components/PlaybookPanel";
import { useAgentEditorDialog } from "@/features/agents/hooks/useAgentEditorDialog";
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

	const [isGalleryOpen, setGalleryOpen] = useState(false);

	const definitionsQuery = useAgentDefinitions();
	const toolCapableModelsQuery = useToolCapableModels();
	const { data: modelsData } = useQuery(withResponseValidation(listLocalModelsOptions()));

	const createMutation = useCreateAgentDefinition();
	const updateMutation = useUpdateAgentDefinition();
	const deleteMutation = useDeleteAgentDefinition();

	const definitions = useMemo(() => definitionsQuery.data ?? [], [definitionsQuery.data]);
	const toolCapableModels = toolCapableModelsQuery.data ?? [];

	const {
		editorTarget,
		editingDefinition,
		isEditorOpen,
		isEditorDirty,
		setIsEditorDirty,
		formRef,
		openCreate,
		openEdit,
		handleCloseEditor,
		requestCloseEditor,
	} = useAgentEditorDialog(definitions);

	const modelOptions = useMemo<AgentModelOption[]>(
		() => (modelsData?.items ?? []).map((model) => ({ value: model.modelName ?? "", label: model.modelName ?? "" })),
		[modelsData],
	);

	const isMutating = createMutation.isPending || updateMutation.isPending || deleteMutation.isPending;
	const submitError =
		createMutation.error || updateMutation.error
			? apiErrorMessage(
					createMutation.error ?? updateMutation.error,
					t("pages.agents.errors.save", "Could not save the agent definition."),
				)
			: undefined;

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
					<InlineErrorAlert
						message={apiErrorMessage(definitionsQuery.error, t("pages.agents.errors.load", "Could not load agent definitions."))}
						data-testid="agent-list-error"
					/>
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
