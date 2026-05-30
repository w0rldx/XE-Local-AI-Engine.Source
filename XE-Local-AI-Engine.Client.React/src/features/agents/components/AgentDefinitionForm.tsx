import { Alert, Button, Group, Select, Stack, Textarea, TextInput } from "@mantine/core";
import { IconDeviceFloppy, IconX } from "@tabler/icons-react";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { AgentToolSelector } from "@/features/agents/components/AgentToolSelector";
import { OrchestrationTopologyEditor } from "@/features/agents/components/OrchestrationTopologyEditor";
import {
	type AgentDefinition,
	agentDefinitionFormSchema,
	type AgentDefinitionFormValues,
	type AgentDefinitionKind,
	agentDefinitionKinds,
	agentReasoningEfforts,
} from "@/features/agents/models/AgentDefinitionModels";
import type { OrchestrationTopology } from "@/features/agents/models/OrchestrationTopologyModels";
import { isModelToolCapable } from "@/features/agents/models/ToolCapability";
import type { ReasoningEffort } from "@/features/chat/models/ChatModels";

export interface AgentModelOption {
	value: string;
	label: string;
}

interface AgentDefinitionFormProps {
	initialValues: AgentDefinitionFormValues;
	modelOptions: readonly AgentModelOption[];
	toolCapableModels: readonly string[];
	// All agent definitions (for the orchestration participant picker). The editing definition is excluded from the
	// specialist list by the editor itself via selfId.
	allDefinitions: readonly AgentDefinition[];
	// The editing definition's id (self / triage). Empty string when creating a new definition.
	selfId: string;
	isSubmitting: boolean;
	submitError?: string;
	onSubmit: (values: AgentDefinitionFormValues) => void;
	onCancel: () => void;
}

const NODE_DEFAULT_MODEL_VALUE = "__node-default__";

// Create/edit form for an agent definition. Controlled Mantine inputs validated with the shared Zod schema on
// submit. Tool selection is gated by the selected model's tool-capability (isModelToolCapable) — when the
// model is not tool-capable the selector is disabled and a warning shows.
export function AgentDefinitionForm({
	initialValues,
	modelOptions,
	toolCapableModels,
	allDefinitions,
	selfId,
	isSubmitting,
	submitError,
	onSubmit,
	onCancel,
}: AgentDefinitionFormProps) {
	const { t } = useTranslation();
	const [values, setValues] = useState<AgentDefinitionFormValues>(initialValues);
	const [fieldErrors, setFieldErrors] = useState<Partial<Record<keyof AgentDefinitionFormValues, string>>>({});
	// Error specific to the orchestration participants field, derived from the Zod result on submit.
	const [participantsError, setParticipantsError] = useState<string | undefined>(undefined);

	const toolCapable = useMemo(
		() => isModelToolCapable(values.modelProfile, toolCapableModels),
		[values.modelProfile, toolCapableModels],
	);

	const modelSelectData = useMemo(
		() => [
			{ value: NODE_DEFAULT_MODEL_VALUE, label: t("pages.agents.form.modelProfile.nodeDefault", "Node default") },
			...modelOptions.map((option) => ({ value: option.value, label: option.label })),
		],
		[modelOptions, t],
	);

	const reasoningEffortData = useMemo(
		() => [
			{ value: NODE_DEFAULT_MODEL_VALUE, label: t("pages.agents.form.reasoningEffort.nodeDefault", "Node default") },
			...agentReasoningEfforts.map((effort) => ({
				value: effort,
				label: t(`pages.agents.form.reasoningEffort.options.${effort}`, effort),
			})),
		],
		[t],
	);

	const kindData = useMemo(
		() =>
			agentDefinitionKinds.map((kind) => ({
				value: kind,
				label: t(`pages.agents.form.kind.options.${kind}`, kind),
			})),
		[t],
	);

	const handleModelChange = useCallback((value: string | null) => {
		const nextModel = value === null || value === NODE_DEFAULT_MODEL_VALUE ? null : value;
		setValues((current) => ({ ...current, modelProfile: nextModel }));
	}, []);

	const handleReasoningEffortChange = useCallback((value: string | null) => {
		const nextEffort = value === null || value === NODE_DEFAULT_MODEL_VALUE ? null : (value as ReasoningEffort);
		setValues((current) => ({ ...current, reasoningEffort: nextEffort }));
	}, []);

	const handleKindChange = useCallback((value: string | null) => {
		if (value === null) {
			return;
		}
		setValues((current) => ({ ...current, kind: value as AgentDefinitionKind }));
	}, []);

	const handleToggleTool = useCallback((toolName: string, selected: boolean) => {
		setValues((current) => {
			const allowedToolNames = selected
				? [...current.allowedToolNames, toolName]
				: current.allowedToolNames.filter((name) => name !== toolName);

			// Drop the approval override for a deselected tool so the stored map never references an unselected tool.
			const toolApprovals = { ...current.toolApprovals };
			if (!selected) {
				delete toolApprovals[toolName];
			}

			return { ...current, allowedToolNames, toolApprovals };
		});
	}, []);

	const handleToggleApproval = useCallback((toolName: string, requiresApproval: boolean) => {
		setValues((current) => ({
			...current,
			toolApprovals: { ...current.toolApprovals, [toolName]: requiresApproval },
		}));
	}, []);

	const handleOrchestrationChange = useCallback((orchestration: OrchestrationTopology) => {
		setValues((current) => ({ ...current, orchestration }));
	}, []);

	const handleSubmit = useCallback(() => {
		const result = agentDefinitionFormSchema.safeParse(values);
		if (!result.success) {
			const nextErrors: Partial<Record<keyof AgentDefinitionFormValues, string>> = {};
			let nextParticipantsError: string | undefined;
			for (const issue of result.error.issues) {
				const key = issue.path[0];
				if (typeof key === "string") {
					nextErrors[key as keyof AgentDefinitionFormValues] = issue.message;
				}
				// Surface the orchestration participants error inline on the participant multi-select.
				if (key === "orchestration" && issue.path[1] === "participantAgentDefinitionIds") {
					nextParticipantsError = issue.message;
				}
			}
			setFieldErrors(nextErrors);
			setParticipantsError(nextParticipantsError);
			return;
		}

		setFieldErrors({});
		setParticipantsError(undefined);
		// When the model is not tool-capable, never persist tools — strip them defensively so a stale selection
		// from before the model was changed cannot leak through.
		const sanitized: AgentDefinitionFormValues = toolCapable
			? values
			: { ...values, allowedToolNames: [], toolApprovals: {} };
		onSubmit(sanitized);
	}, [onSubmit, toolCapable, values]);

	return (
		<Stack gap="md" data-testid="agent-definition-form">
			<TextInput
				label={t("pages.agents.form.name.label", "Name")}
				placeholder={t("pages.agents.form.name.placeholder", "Research assistant")}
				value={values.name}
				required={true}
				error={fieldErrors.name ? t("pages.agents.form.name.required", "Name is required") : undefined}
				onChange={(event) => setValues((current) => ({ ...current, name: event.currentTarget.value }))}
				data-testid="agent-form-name"
			/>
			<Textarea
				label={t("pages.agents.form.description.label", "Description")}
				placeholder={t("pages.agents.form.description.placeholder", "Optional short summary")}
				value={values.description}
				autosize={true}
				minRows={2}
				onChange={(event) => setValues((current) => ({ ...current, description: event.currentTarget.value }))}
				data-testid="agent-form-description"
			/>
			<Textarea
				label={t("pages.agents.form.instructions.label", "Instructions")}
				description={t(
					"pages.agents.form.instructions.description",
					"System prompt that defines how this agent behaves.",
				)}
				placeholder={t("pages.agents.form.instructions.placeholder", "You are a helpful assistant that…")}
				value={values.instructions}
				required={true}
				autosize={true}
				minRows={4}
				error={
					fieldErrors.instructions
						? t("pages.agents.form.instructions.required", "Instructions are required")
						: undefined
				}
				onChange={(event) => setValues((current) => ({ ...current, instructions: event.currentTarget.value }))}
				data-testid="agent-form-instructions"
			/>
			<Group grow={true} align="flex-start">
				<Select
					label={t("pages.agents.form.kind.label", "Kind")}
					data={kindData}
					value={values.kind}
					allowDeselect={false}
					onChange={handleKindChange}
					data-testid="agent-form-kind"
				/>
				<Select
					label={t("pages.agents.form.modelProfile.label", "Model profile")}
					data={modelSelectData}
					value={values.modelProfile ?? NODE_DEFAULT_MODEL_VALUE}
					allowDeselect={false}
					onChange={handleModelChange}
					data-testid="agent-form-model-profile"
				/>
				<Select
					label={t("pages.agents.form.reasoningEffort.label", "Reasoning effort")}
					data={reasoningEffortData}
					value={values.reasoningEffort ?? NODE_DEFAULT_MODEL_VALUE}
					allowDeselect={false}
					onChange={handleReasoningEffortChange}
					data-testid="agent-form-reasoning-effort"
				/>
			</Group>
			{values.kind === "Orchestrator" ? (
				<OrchestrationTopologyEditor
					topology={values.orchestration}
					candidateDefinitions={allDefinitions}
					selfId={selfId}
					triageName={values.name}
					orchestratorModelProfile={values.modelProfile}
					toolCapableModels={toolCapableModels}
					participantsError={participantsError}
					onChange={handleOrchestrationChange}
				/>
			) : null}
			<AgentToolSelector
				selectedToolNames={values.allowedToolNames}
				toolApprovals={values.toolApprovals}
				toolCapable={toolCapable}
				onToggleTool={handleToggleTool}
				onToggleApproval={handleToggleApproval}
			/>
			{submitError ? (
				<Alert color="red" data-testid="agent-form-submit-error">
					{submitError}
				</Alert>
			) : null}
			<Group justify="flex-end">
				<Button
					variant="subtle"
					leftSection={<IconX size={16} />}
					onClick={onCancel}
					disabled={isSubmitting}
					data-testid="agent-form-cancel"
				>
					{t("common.cancel", "Cancel")}
				</Button>
				<Button
					leftSection={<IconDeviceFloppy size={16} />}
					onClick={handleSubmit}
					loading={isSubmitting}
					data-testid="agent-form-submit"
				>
					{t("common.save", "Save")}
				</Button>
			</Group>
		</Stack>
	);
}
