import { Divider, Group, Select, Stack, Switch } from "@mantine/core";
import { useCallback, useMemo } from "react";
import { useTranslation } from "react-i18next";

import type { ReasoningEffort } from "@/core/models/ReasoningEffort";
import {
	type AgentDefinitionFormValues,
	type AgentDefinitionKind,
	agentDefinitionKinds,
	agentReasoningEfforts,
} from "@/features/agents/models/AgentDefinitionModels";

import type { AgentModelOption } from "./AgentDefinitionForm.types";

const NODE_DEFAULT_MODEL_VALUE = "__node-default__";

interface AgentDefinitionModelFieldsProps {
	values: AgentDefinitionFormValues;
	modelOptions: readonly AgentModelOption[];
	onFieldChange: (updater: (current: AgentDefinitionFormValues) => AgentDefinitionFormValues) => void;
}

// Kind, model profile, reasoning effort selects + adaptive memory / temporary chat / memory extraction switches.
export function AgentDefinitionModelFields({ values, modelOptions, onFieldChange }: AgentDefinitionModelFieldsProps) {
	const { t } = useTranslation();

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

	const handleModelChange = useCallback(
		(value: string | null) => {
			const nextModel = value === null || value === NODE_DEFAULT_MODEL_VALUE ? null : value;
			onFieldChange((current) => ({ ...current, modelProfile: nextModel }));
		},
		[onFieldChange],
	);

	const handleReasoningEffortChange = useCallback(
		(value: string | null) => {
			const nextEffort = value === null || value === NODE_DEFAULT_MODEL_VALUE ? null : (value as ReasoningEffort);
			onFieldChange((current) => ({ ...current, reasoningEffort: nextEffort }));
		},
		[onFieldChange],
	);

	const handleKindChange = useCallback(
		(value: string | null) => {
			if (value === null) {
				return;
			}
			onFieldChange((current) => ({ ...current, kind: value as AgentDefinitionKind }));
		},
		[onFieldChange],
	);

	return (
		<Stack gap="md">
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
			<Switch
				label={t("pages.agents.form.playbookEnabled.label", "Enable adaptive memory")}
				description={t(
					"pages.agents.form.playbookEnabled.description",
					"Append this agent's enabled playbook actions to its instructions at run time.",
				)}
				checked={values.playbookEnabled}
				onChange={(event) => {
					const checked = event.currentTarget.checked;
					onFieldChange((current) => ({ ...current, playbookEnabled: checked }));
				}}
				data-testid="agent-form-playbook-enabled"
			/>
			{/* Only meaningful when adaptive memory is enabled — defaulting conversations to temporary suppresses
			    learning new memory from them. Shown alongside the memory toggle so the pairing is obvious. */}
			{values.playbookEnabled ? (
				<Switch
					label={t("pages.agents.form.defaultTemporaryChat.label", "Default new chats to temporary")}
					description={t(
						"pages.agents.form.defaultTemporaryChat.description",
						"New conversations with this agent won't teach it new memory by default; they still use existing memory. Each chat can override this.",
					)}
					checked={values.defaultTemporaryChat}
					onChange={(event) => {
						const checked = event.currentTarget.checked;
						onFieldChange((current) => ({ ...current, defaultTemporaryChat: checked }));
					}}
					data-testid="agent-form-default-temporary-chat"
				/>
			) : null}
			{/* Distinct from the toggle above: this controls whether the agent LEARNS new memory from its runs. Off =
			    retrieval-only — it still uses existing memory but skips the post-run extraction round-trip. */}
			{values.playbookEnabled ? (
				<Switch
					label={t("pages.agents.form.memoryExtractionEnabled.label", "Learn new memory from runs")}
					description={t(
						"pages.agents.form.memoryExtractionEnabled.description",
						"Let this agent learn new memory from its runs. Off = use existing memory only (no new memory is mined).",
					)}
					checked={values.memoryExtractionEnabled}
					onChange={(event) => {
						const checked = event.currentTarget.checked;
						onFieldChange((current) => ({ ...current, memoryExtractionEnabled: checked }));
					}}
					data-testid="agent-form-memory-extraction-enabled"
				/>
			) : null}
			<Divider label={t("pages.agents.form.advanced.label", "Advanced")} labelPosition="left" mt="sm" />
			<Switch
				label={t("pages.agents.form.disableBaseScaffold.label", "Disable base instructions scaffold")}
				description={t(
					"pages.agents.form.disableBaseScaffold.description",
					"Skip the layered base instructions this node normally prepends to every agent — use only the instructions written above, unmodified.",
				)}
				checked={values.disableBaseScaffold}
				onChange={(event) => {
					const checked = event.currentTarget.checked;
					onFieldChange((current) => ({ ...current, disableBaseScaffold: checked }));
				}}
				data-testid="agent-form-disable-base-scaffold"
			/>
			<Switch
				label={t("pages.agents.form.disableToolRelevanceFilter.label", "Always offer every tool")}
				description={t(
					"pages.agents.form.disableToolRelevanceFilter.description",
					"Skip this node's tool-relevance filter for this agent, so every tool it may use is shown to the model on every round. Turn this on if the agent misses tools; it costs context on agents with many tools.",
				)}
				checked={values.disableToolRelevanceFilter}
				onChange={(event) => {
					const checked = event.currentTarget.checked;
					onFieldChange((current) => ({ ...current, disableToolRelevanceFilter: checked }));
				}}
				data-testid="agent-form-disable-tool-relevance-filter"
			/>
		</Stack>
	);
}
