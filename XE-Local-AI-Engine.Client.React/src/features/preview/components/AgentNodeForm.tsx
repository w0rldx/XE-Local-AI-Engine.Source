import { Group, Select, Stack, Text, Textarea, TextInput } from "@mantine/core";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { useQuery } from "@tanstack/react-query";

import { toChatModelOptions } from "@/features/chat/pages/ChatModelOptions";
import { useAgentDefinitions } from "@/features/agents/queries/useAgentDefinitions";
import type { PreviewCanvasNodeData } from "@/features/preview/models/PreviewCanvasModels";

// Reasoning-effort options offered for a preview Agent node. Reuses the agent surface's set ("none" plus graded
// efforts); a null reasoningEffort = provider default, surfaced as the empty option.
const REASONING_EFFORTS = ["none", "low", "medium", "high"] as const;

export interface AgentNodeFormProps {
	// The current Agent node data being edited. Only the agent fields are read/written here.
	readonly data: PreviewCanvasNodeData;
	// Patch one or more agent fields on the selected node. The canvas owns the node array; this form only emits
	// field patches so the node update stays a single source of truth.
	readonly onChange: (patch: Partial<PreviewCanvasNodeData>) => void;
}

// Per-node configuration for an Agent block: label, instructions, model (chat-capable picker reused from chat),
// optional reasoning effort, and an Import-from-AgentDefinition copy picker. Import COPIES the chosen definition's
// instructions, label, and reasoning effort into this node — it does NOT (and cannot) set the node model, because
// an AgentDefinition has no concrete model field (only a modelProfile family hint, not a picker value). Import does
// not link the node to the definition, so a later edit of either side is independent (a one-time copy, mirroring how
// the agent form imports a template).
export function AgentNodeForm({ data, onChange }: AgentNodeFormProps) {
	const { t } = useTranslation();
	const agentDefinitionsQuery = useAgentDefinitions();

	// Chat-capable models, reusing the chat picker's filter (strict: only kind === "Chat"). The local-default
	// option is omitted here — a preview Agent node must name a concrete model (the backend validator rejects an
	// agent without one), so the picker offers only real models.
	const localModelsQuery = useQuery({
		...withResponseValidation(listLocalModelsOptions()),
	});
	const modelOptions = useMemo(() => {
		const response = localModelsQuery.data;
		if (!response) {
			return [];
		}
		return toChatModelOptions(response.items ?? [], response.isAvailable ?? false).map((option) => ({
			value: option.value,
			label: option.label,
		}));
	}, [localModelsQuery.data]);

	const importOptions = useMemo(
		() => (agentDefinitionsQuery.data ?? []).map((definition) => ({ value: definition.id, label: definition.name })),
		[agentDefinitionsQuery.data],
	);

	// Copy the chosen definition's instructions, label, and reasoning effort into this node. The node model is left
	// untouched — an AgentDefinition has no concrete model to copy (only a modelProfile family hint).
	const handleImport = (definitionId: string | null): void => {
		if (definitionId === null) {
			return;
		}
		const definition = (agentDefinitionsQuery.data ?? []).find((candidate) => candidate.id === definitionId);
		if (definition === undefined) {
			return;
		}
		onChange({
			instructions: definition.instructions,
			label: data.label?.trim() ? data.label : definition.name,
			reasoningEffort: definition.reasoningEffort ?? data.reasoningEffort,
		});
	};

	return (
		<Stack gap="sm" data-testid="preview-agent-node-form">
			<Group justify="space-between" align="flex-end" wrap="nowrap">
				<TextInput
					style={{ flex: 1 }}
					label={t("pages.preview.agentForm.label.label", "Label")}
					placeholder={t("pages.preview.agentForm.label.placeholder", "Agent")}
					value={data.label ?? ""}
					onChange={(event) => onChange({ label: event.currentTarget.value })}
					data-testid="preview-agent-label"
				/>
				<Select
					label={t("pages.preview.agentForm.import.label", "Import from agent")}
					placeholder={t("pages.preview.agentForm.import.placeholder", "Copy instructions…")}
					data={importOptions}
					value={null}
					onChange={handleImport}
					searchable={true}
					clearable={true}
					nothingFoundMessage={t("pages.preview.agentForm.import.empty", "No agent definitions")}
					data-testid="preview-agent-import"
				/>
			</Group>

			<Select
				label={t("pages.preview.agentForm.model.label", "Model")}
				placeholder={t("pages.preview.agentForm.model.placeholder", "Select a chat-capable model")}
				data={modelOptions}
				value={data.model ?? null}
				onChange={(value) => onChange({ model: value ?? undefined })}
				searchable={true}
				nothingFoundMessage={t("pages.preview.agentForm.model.empty", "No chat-capable models")}
				data-testid="preview-agent-model"
			/>

			<Select
				label={t("pages.preview.agentForm.reasoning.label", "Reasoning effort")}
				placeholder={t("pages.preview.agentForm.reasoning.placeholder", "Provider default")}
				data={REASONING_EFFORTS.map((effort) => ({
					value: effort,
					label: t(`pages.preview.agentForm.reasoning.options.${effort}`, effort),
				}))}
				value={data.reasoningEffort ?? null}
				onChange={(value) => onChange({ reasoningEffort: value ?? undefined })}
				clearable={true}
				data-testid="preview-agent-reasoning"
			/>

			<Textarea
				label={t("pages.preview.agentForm.instructions.label", "Instructions")}
				placeholder={t("pages.preview.agentForm.instructions.placeholder", "System instructions for this agent…")}
				value={data.instructions ?? ""}
				onChange={(event) => onChange({ instructions: event.currentTarget.value })}
				autosize={true}
				minRows={4}
				maxRows={12}
				data-testid="preview-agent-instructions"
			/>

			<Text size="xs" c="dimmed" data-testid="preview-agent-form-hint">
				{t("pages.preview.agentForm.autosaveHint", "Changes apply to the canvas immediately")}
			</Text>
		</Stack>
	);
}
