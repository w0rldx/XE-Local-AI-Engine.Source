import { Alert, Button, Card, Container, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconPlus, IconRobot, IconSparkles } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { AgentDefinitionForm, type AgentModelOption } from "@/features/agents/components/AgentDefinitionForm";
import { AgentDefinitionList } from "@/features/agents/components/AgentDefinitionList";
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

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

const emptyFormValues: AgentDefinitionFormValues = {
	name: "",
	description: "",
	instructions: "",
	modelProfile: null,
	reasoningEffort: null,
	kind: "Single",
	allowedToolNames: [],
	toolApprovals: {},
	orchestration: emptyOrchestrationTopology(),
	playbookEnabled: false,
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
		// Round-trip the persisted topology back into the editor (strips the triage from the specialist list).
		orchestration: deserializeOrchestrationTopology(definition.orchestrationTopologyJson).topology,
		playbookEnabled: definition.playbookEnabled,
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

	const definitionsQuery = useAgentDefinitions();
	const toolCapableModelsQuery = useToolCapableModels();
	const modelsQuery = useQuery(withResponseValidation(listLocalModelsOptions()));

	const createMutation = useCreateAgentDefinition();
	const updateMutation = useUpdateAgentDefinition();
	const deleteMutation = useDeleteAgentDefinition();

	const definitions = definitionsQuery.data ?? [];
	const toolCapableModels = toolCapableModelsQuery.data ?? [];

	const modelOptions = useMemo<AgentModelOption[]>(
		() => (modelsQuery.data?.items ?? []).map((model) => ({ value: model.modelName ?? "", label: model.modelName ?? "" })),
		[modelsQuery.data],
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
			? errorMessage(
					createMutation.error ?? updateMutation.error,
					t("pages.agents.errors.save", "Could not save the agent definition."),
				)
			: undefined;

	const handleSubmit = useCallback(
		(values: AgentDefinitionFormValues) => {
			if (editorTarget?.mode === "edit") {
				// On edit the triage (this orchestrator) is the definition's own id, pinning the topology to it.
				const request = toSaveAgentDefinitionRequest(values, editorTarget.id);
				updateMutation.mutate({ id: editorTarget.id, request }, { onSuccess: () => closeEditor() });
				return;
			}

			// On create the id is unknown; the triage is assigned by the backend and re-pinned on the next edit.
			const request = toSaveAgentDefinitionRequest(values);
			createMutation.mutate(request, { onSuccess: () => closeEditor() });
		},
		[closeEditor, createMutation, editorTarget, updateMutation],
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
				deleteMutation.mutate(definition.id);
			}
		},
		[confirm, deleteMutation, t],
	);

	const isEditorOpen = editorTarget !== null;
	const formInitialValues = editingDefinition ? toFormValues(editingDefinition) : emptyFormValues;

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Group justify="space-between" align="flex-start">
					<Stack gap={4}>
						<Text size="sm" tt="uppercase" fw={700} c="dimmed">
							{t("pages.agents.eyebrow", "Worker Node")}
						</Text>
						<Group gap="xs" align="center">
							<IconRobot size={24} />
							<Title order={2}>{t("pages.agents.title", "Agent definitions")}</Title>
						</Group>
						<Text c="dimmed">
							{t(
								"pages.agents.subtitle",
								"Author local agent personas: instructions, model, reasoning effort, and the tools they may use.",
							)}
						</Text>
					</Stack>
					{!isEditorOpen ? (
						<Group gap="sm">
							<Button
								variant="default"
								leftSection={<IconSparkles size={16} />}
								onClick={() => setGalleryOpen(true)}
								data-testid="agent-templates-button"
							>
								{t("pages.agents.templatesButton", "Add starter agents")}
							</Button>
							<Button leftSection={<IconPlus size={16} />} onClick={openCreate} data-testid="agent-create-button">
								{t("pages.agents.createButton", "New agent")}
							</Button>
						</Group>
					) : null}
				</Group>

				{deleteMutation.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="agent-delete-error">
						{errorMessage(deleteMutation.error, t("pages.agents.errors.delete", "Could not delete the agent."))}
					</Alert>
				) : null}

				{isEditorOpen ? (
					<Card withBorder={true} radius="md" p="lg" data-testid="agent-editor-card">
						<Stack gap="md">
							<Title order={3}>
								{editorTarget?.mode === "edit"
									? t("pages.agents.editor.editTitle", "Edit agent")
									: t("pages.agents.editor.createTitle", "New agent")}
							</Title>
							<AgentDefinitionForm
								key={editorTarget?.mode === "edit" ? editorTarget.id : "create"}
								initialValues={formInitialValues}
								modelOptions={modelOptions}
								toolCapableModels={toolCapableModels}
								allDefinitions={definitions}
								selfId={editorTarget?.mode === "edit" ? editorTarget.id : ""}
								isSubmitting={createMutation.isPending || updateMutation.isPending}
								submitError={submitError}
								onSubmit={handleSubmit}
								onCancel={closeEditor}
							/>
							{/* Per-agent playbook governance (Playbook P1). Only meaningful for a persisted agent (has an id);
							    a brand-new agent must be saved first. Capability-gated under agentManagement. */}
							{editingDefinition ? (
								<PlaybookPanel
									agentDefinitionId={editingDefinition.id}
									agentName={editingDefinition.name}
									enabled={nodeCapabilities.agentManagement}
								/>
							) : null}
							{/* Per-agent read-only feedback insights (Playbook P2). Only meaningful for a persisted
							    agent (has an id). Capability-gated under agentManagement; analytics-only, no mutations. */}
							{editingDefinition ? (
								<FeedbackInsightsPanel
									agentDefinitionId={editingDefinition.id}
									agentName={editingDefinition.name}
									enabled={nodeCapabilities.agentManagement}
								/>
							) : null}
							{/* Per-agent golden conversation set (Playbook P4). The eval gate replays these cases against a
							    candidate action before promotion. Only meaningful for a persisted agent (has an id).
							    Capability-gated under agentManagement. */}
							{editingDefinition ? (
								<GoldenConversationPanel
									agentDefinitionId={editingDefinition.id}
									agentName={editingDefinition.name}
									enabled={nodeCapabilities.agentManagement}
								/>
							) : null}
						</Stack>
					</Card>
				) : (
					<Card withBorder={true} radius="md" p="lg">
						<Stack gap="md">
							{definitionsQuery.isLoading ? (
								<Group gap="sm">
									<Loader size="sm" />
									<Text c="dimmed">{t("pages.agents.list.loading", "Loading agent definitions…")}</Text>
								</Group>
							) : null}
							{definitionsQuery.error ? (
								<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="agent-list-error">
									{errorMessage(definitionsQuery.error, t("pages.agents.errors.load", "Could not load agent definitions."))}
								</Alert>
							) : null}
							{!definitionsQuery.isLoading && !definitionsQuery.error ? (
								<AgentDefinitionList
									definitions={definitions}
									isMutating={isMutating}
									onEdit={openEdit}
									onDelete={handleDelete}
								/>
							) : null}
						</Stack>
					</Card>
				)}

				<AgentTemplateGallery opened={isGalleryOpen} onClose={() => setGalleryOpen(false)} />
			</Stack>
		</Container>
	);
}
