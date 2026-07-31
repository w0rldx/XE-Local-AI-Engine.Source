import { Alert, Stack } from "@mantine/core";
import { type Ref, useCallback, useImperativeHandle, useMemo, useRef, useState } from "react";

import { AgentDefinitionBasicFields } from "@/features/agents/components/AgentDefinitionBasicFields";
import { AgentDefinitionModelFields } from "@/features/agents/components/AgentDefinitionModelFields";
import { AgentSkillSelector } from "@/features/agents/components/AgentSkillSelector";
import { AgentToolSelector } from "@/features/agents/components/AgentToolSelector";
import { OrchestrationTopologyEditor } from "@/features/agents/components/OrchestrationTopologyEditor";
import {
	type AgentDefinition,
	type AgentDefinitionFormValues,
	agentDefinitionFormSchema,
} from "@/features/agents/models/AgentDefinitionModels";
import type { OrchestrationTopology } from "@/features/agents/models/OrchestrationTopologyModels";
import { isModelToolCapable } from "@/features/agents/models/ToolCapability";

import type { AgentModelOption } from "./AgentDefinitionForm.types";

export type { AgentModelOption } from "./AgentDefinitionForm.types";

// Imperative handle exposing submit() so a host (the dialog footer's Save button) can trigger the form's own
// validate-then-submit. The footer button calls submit(); validation stays in the form.
export interface AgentDefinitionFormHandle {
	submit: () => void;
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
	submitError?: string;
	onSubmit: (values: AgentDefinitionFormValues) => void;
	// Reports whether the form has unsaved edits (current values differ from initialValues). The page wires this to the
	// dialog close-guard and the route nav-guard. The form no longer renders its own Save/Cancel — those live in the
	// dialog footer and drive submission via the imperative handle.
	onDirtyChange?: (isDirty: boolean) => void;
	/** Imperative handle exposing submit() so the host dialog footer can drive submission. */
	ref?: Ref<AgentDefinitionFormHandle>;
}

// Create/edit form for an agent definition. Controlled Mantine inputs validated with the shared Zod schema on
// submit. Tool selection is gated by the selected model's tool-capability (isModelToolCapable) — when the
// model is not tool-capable the selector is disabled and a warning shows.
export function AgentDefinitionForm({
	initialValues,
	modelOptions,
	toolCapableModels,
	allDefinitions,
	selfId,
	submitError,
	onSubmit,
	onDirtyChange,
	ref,
}: AgentDefinitionFormProps) {
	const [values, setValues] = useState<AgentDefinitionFormValues>(initialValues);
	const [fieldErrors, setFieldErrors] = useState<Partial<Record<keyof AgentDefinitionFormValues, string>>>({});
	// Error specific to the orchestration participants field, derived from the Zod result on submit.
	const [participantsError, setParticipantsError] = useState<string | undefined>(undefined);

	// Update values AND report the resulting dirty state to the host in the same event-driven step. Dirtiness is
	// derived (current values vs. the mount snapshot, which the page keys stable per editor session), so reporting
	// it from the updater — rather than a useEffect that watches state — avoids the extra parent re-render the
	// effect-sync pattern causes. A JSON compare is sufficient: the value shape is plain data.
	const updateValues = useCallback(
		(updater: (current: AgentDefinitionFormValues) => AgentDefinitionFormValues) => {
			setValues((current) => {
				const next = updater(current);
				onDirtyChange?.(JSON.stringify(next) !== JSON.stringify(initialValues));
				return next;
			});
		},
		[initialValues, onDirtyChange],
	);

	// Report the initial (clean) dirty state once on mount. The page keys this component per editor target, so a
	// fresh mount always starts clean. Done as a one-shot render-time call (ref-guarded) rather than a useEffect that
	// re-syncs on every change — the latter forces an extra parent re-render per keystroke.
	const didReportInitialDirty = useRef(false);
	if (!didReportInitialDirty.current) {
		didReportInitialDirty.current = true;
		onDirtyChange?.(JSON.stringify(values) !== JSON.stringify(initialValues));
	}

	const toolCapable = useMemo(
		() => isModelToolCapable(values.modelProfile, toolCapableModels),
		[values.modelProfile, toolCapableModels],
	);

	const handleToggleTool = useCallback(
		(toolName: string, selected: boolean) => {
			updateValues((current) => {
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
		},
		[updateValues],
	);

	const handleToggleApproval = useCallback(
		(toolName: string, requiresApproval: boolean) => {
			updateValues((current) => ({
				...current,
				toolApprovals: { ...current.toolApprovals, [toolName]: requiresApproval },
			}));
		},
		[updateValues],
	);

	const handleToggleSkill = useCallback(
		(skillId: string, selected: boolean) => {
			updateValues((current) => {
				const allowedSkillIds = selected
					? [...current.allowedSkillIds, skillId]
					: current.allowedSkillIds.filter((id) => id !== skillId);
				return { ...current, allowedSkillIds };
			});
		},
		[updateValues],
	);

	const handleOrchestrationChange = useCallback(
		(orchestration: OrchestrationTopology) => {
			updateValues((current) => ({ ...current, orchestration }));
		},
		[updateValues],
	);

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
		const sanitized: AgentDefinitionFormValues = toolCapable ? values : { ...values, allowedToolNames: [], toolApprovals: {} };
		onSubmit(sanitized);
	}, [onSubmit, toolCapable, values]);

	useImperativeHandle(ref, () => ({ submit: handleSubmit }), [handleSubmit]);

	return (
		<Stack gap="md" data-testid="agent-definition-form">
			<AgentDefinitionBasicFields
				values={values}
				nameError={fieldErrors.name}
				instructionsError={fieldErrors.instructions}
				onFieldChange={updateValues}
			/>
			<AgentDefinitionModelFields
				values={values}
				modelOptions={modelOptions}
				onFieldChange={updateValues}
			/>
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
			<AgentSkillSelector selectedSkillIds={values.allowedSkillIds} onToggleSkill={handleToggleSkill} />
			{submitError ? (
				<Alert color="red" data-testid="agent-form-submit-error">
					{submitError}
				</Alert>
			) : null}
		</Stack>
	);
}
