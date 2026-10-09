import { Card, Group, NumberInput, Select, Stack, Switch, TagsInput, Text, TextInput, Title } from "@mantine/core";
import { IconBrain, IconRobot, IconServer, IconTool } from "@tabler/icons-react";
import { type ReactNode, useMemo } from "react";
import { useTranslation } from "react-i18next";

import {
	NodeSettingsAgentLimitsCard,
	NodeSettingsAgentWorkspacesCard,
} from "@/features/node-settings/components/NodeSettingsAdvancedFieldsCard";
import { NodeSettingsExternalAccessCard } from "@/features/node-settings/components/NodeSettingsExternalAccessCard";
import { NodeSettingsFeaturesCard } from "@/features/node-settings/components/NodeSettingsFeaturesCard";
import {
	nodeSettingsFieldError,
	nodeSettingsFieldLabel,
} from "@/features/node-settings/components/NodeSettingsFieldPresentation";
import { NodeSettingsKnowledgeModelsCard } from "@/features/node-settings/components/NodeSettingsKnowledgeModelsCard";
import { NodeSettingsOllamaCard } from "@/features/node-settings/components/NodeSettingsOllamaCard";
import { NodeSettingsRuntimeCard } from "@/features/node-settings/components/NodeSettingsRuntimeCard";
import { NodeSettingsUiModeCard } from "@/features/node-settings/components/NodeSettingsUiModeCard";
import { NodeSettingsUsageRatesCard } from "@/features/node-settings/components/NodeSettingsUsageRatesCard";
import {
	NodeSettingsAgentRunLimitsCard,
	NodeSettingsCloudAccessCard,
	NodeSettingsContainerRuntimeCard,
	NodeSettingsContextCompactionCard,
	NodeSettingsDownloadLimitsCard,
	NodeSettingsKnowledgeRetrievalCard,
	NodeSettingsKnowledgeSearchCard,
	NodeSettingsOutputCapCard,
	NodeSettingsReasoningBudgetsCard,
	NodeSettingsRetentionCard,
	NodeSettingsToolLimitsCard,
	NodeSettingsTranscriptionCard,
	NodeSettingsWebFetchLimitsCard,
	NodeSettingsWorkspaceLimitsCard,
} from "@/features/node-settings/components/NodeSettingsTunableCards";
import { NodeSettingsWebAccessCard } from "@/features/node-settings/components/NodeSettingsWebAccessCard";
import type {
	ExternalAccessPreset,
	NodeSettingsFieldBounds,
	NodeSettingsFieldsForm,
	NodeSettingsModelOption,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";
import type { NodeSettingsSectionId } from "@/features/node-settings/models/NodeSettingsSections";

// Presentational: the draft-bound cards of ONE section. The page owns the form state, bounds, errors and the change
// handlers and composes the section's action panels around this. Developer-only cards render only when
// `showDeveloperFields` is set, so an off-mode save can never touch a hidden field — they are not even mounted.
export interface NodeSettingsFieldsCardProps {
	readonly section: NodeSettingsSectionId;
	readonly form: NodeSettingsFieldsForm;
	readonly bounds: NodeSettingsFieldBounds;
	readonly errors: Readonly<Record<string, string>>;
	readonly onChange: <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]) => void;
	// Applies an external-access profile: the page moves all three switches and marks the preset pending, so the save
	// carries the profile name alone (the server derives the switches from it).
	readonly onApplyPreset: (preset: ExternalAccessPreset) => void;
	// When true, the developer-only cards are rendered. Driven by the page's developer-mode flag.
	readonly showDeveloperFields: boolean;
	// Installed chat-capable models offered as the draft model for draft-* speculative modes.
	readonly draftModelOptions: readonly NodeSettingsModelOption[];
	// Installed llama.cpp chat models eligible for the supervised keep-warm loop.
	readonly keepWarmModelOptions: readonly NodeSettingsModelOption[];
	readonly autoEffortFastModelOptions: readonly NodeSettingsModelOption[];
	// Installed node-local chat models (GGUF and Ollama) offered to the playbook analysis, eval and memory extraction runs.
	readonly backgroundModelOptions: readonly NodeSettingsModelOption[];
	// All installed models offered as the knowledge-base reranker (reranker GGUFs are not a chat kind, so this list is
	// not filtered to chat-capable models).
	readonly rerankerModelOptions: readonly NodeSettingsModelOption[];
	// One-click download of the node's recommended reranker GGUF. The page owns the mutation + progress feed; this
	// component only renders the button and reflects its pending / in-flight state.
	readonly onDownloadRecommendedReranker: () => void;
	readonly isDownloadRecommendedRerankerPending: boolean;
	readonly isRecommendedRerankerInFlight: boolean;
	// One-click download of the node's recommended embedding GGUF. Unlike the reranker, the embedding model is not a
	// node-settings field (nothing to select/save) — the knowledge base just needs one installed to index documents.
	readonly onDownloadRecommendedEmbedding: () => void;
	readonly isDownloadRecommendedEmbeddingPending: boolean;
	readonly isRecommendedEmbeddingInFlight: boolean;
	// True only when the node has definitely reported the optional Ollama runtime gated off; the page owns that probe
	// and fails open.
	readonly ollamaRuntimeDisabled: boolean;
	// The update-channel picker, supplied by the route (it belongs to another feature). Rendered in External access.
	readonly updateChannelSelector?: ReactNode;
}

export function NodeSettingsFieldsCard(props: NodeSettingsFieldsCardProps) {
	const { section, form, bounds, errors, onChange, showDeveloperFields } = props;
	const { t } = useTranslation();
	const tunable = { form, bounds, errors, onChange };

	switch (section) {
		case "general":
			return (
				<>
					<NodeSettingsUiModeCard value={form.uiMode} onChange={(value) => onChange("uiMode", value)} />
					<NodeSettingsFeaturesCard form={form} errors={errors} onChange={onChange} />
				</>
			);
		case "chat":
			return (
				<>
					<LocalChatCard {...props} />
					<NodeSettingsReasoningBudgetsCard {...tunable} />
					<NodeSettingsOutputCapCard {...tunable} />
					<NodeSettingsToolLimitsCard {...tunable} />
					<NodeSettingsAgentRunLimitsCard {...tunable} />
					<NodeSettingsContextCompactionCard {...tunable} />
					{showDeveloperFields ? (
						<NodeSettingsAgentLimitsCard form={form} bounds={bounds} errors={errors} onChange={onChange} />
					) : null}
				</>
			);
		case "runtime":
			return (
				<NodeSettingsRuntimeCard
					form={form}
					bounds={bounds}
					errors={errors}
					onChange={onChange}
					draftModelOptions={props.draftModelOptions}
					keepWarmModelOptions={props.keepWarmModelOptions}
				/>
			);
		case "runtimes":
			return (
				<>
					<NodeSettingsOllamaCard
						form={form}
						errors={errors}
						onChange={onChange}
						ollamaRuntimeDisabled={props.ollamaRuntimeDisabled}
					/>
					<NodeSettingsContainerRuntimeCard {...tunable} />
				</>
			);
		case "models":
			return (
				<>
					<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-hf-card">
						<Stack gap="md">
							<Group justify="space-between" align="center">
								<Title order={2} size="h4">
									{t("pages.nodeSettings.fields.huggingFace.title", "Hugging Face")}
								</Title>
								<IconServer size={20} />
							</Group>
							<TextInput
								label={nodeSettingsFieldLabel(
									t,
									"huggingFaceDefaultQuant",
									t("pages.nodeSettings.fields.huggingFaceDefaultQuant.label", "Default quantization"),
								)}
								description={t(
									"pages.nodeSettings.fields.huggingFaceDefaultQuant.description",
									"Preferred GGUF quantization when downloading from Hugging Face (e.g. Q4_K_M).",
								)}
								value={form.huggingFaceDefaultQuant}
								onChange={(event) => onChange("huggingFaceDefaultQuant", event.currentTarget.value)}
								data-testid="node-settings-hf-default-quant"
							/>
						</Stack>
					</Card>
					<NodeSettingsDownloadLimitsCard {...tunable} />
				</>
			);
		case "knowledge":
			return (
				<>
					<NodeSettingsKnowledgeModelsCard
						form={form}
						errors={errors}
						onChange={onChange}
						rerankerModelOptions={props.rerankerModelOptions}
						rerankerDownload={{
							onStart: props.onDownloadRecommendedReranker,
							pending: props.isDownloadRecommendedRerankerPending,
							inFlight: props.isRecommendedRerankerInFlight,
						}}
						embeddingDownload={{
							onStart: props.onDownloadRecommendedEmbedding,
							pending: props.isDownloadRecommendedEmbeddingPending,
							inFlight: props.isRecommendedEmbeddingInFlight,
						}}
					/>
					<NodeSettingsKnowledgeSearchCard {...tunable} />
					<NodeSettingsKnowledgeRetrievalCard {...tunable} />
					<BackgroundModelsCard {...props} />
					<NodeSettingsWebAccessCard form={form} errors={errors} onChange={onChange} />
					<NodeSettingsWebFetchLimitsCard {...tunable} />
				</>
			);
		case "privacy":
			return (
				<>
					<NodeSettingsExternalAccessCard
						form={form}
						onChange={onChange}
						onApplyPreset={props.onApplyPreset}
						updateChannelSelector={props.updateChannelSelector}
					/>
					<NodeSettingsCloudAccessCard {...tunable} />
				</>
			);
		case "workspaces":
			return (
				<>
					<NodeSettingsWorkspaceLimitsCard {...tunable} />
					{showDeveloperFields ? (
						<NodeSettingsAgentWorkspacesCard form={form} bounds={bounds} errors={errors} onChange={onChange} />
					) : (
						<Text c="dimmed" data-testid="node-settings-developer-only-note">
							{t(
								"pages.nodeSettings.developerOnlyNote",
								"The AgentHome limits are developer settings. Turn on developer mode under General to change them.",
							)}
						</Text>
					)}
				</>
			);
		case "usage":
			return (
				<>
					<NodeSettingsUsageRatesCard
						usageRates={form.usageRates}
						error={nodeSettingsFieldError(t, errors, "usageRates")}
						onChange={(usageRates) => onChange("usageRates", usageRates)}
					/>
					<NodeSettingsRetentionCard {...tunable} />
					<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-worker-card">
						<Stack gap="md">
							<Group justify="space-between" align="center">
								<Title order={2} size="h4">
									{t("pages.nodeSettings.fields.worker.title", "Worker limits")}
								</Title>
								<IconTool size={20} />
							</Group>
							<NumberInput
								label={nodeSettingsFieldLabel(
									t,
									"maxResponseSizeMb",
									t("pages.nodeSettings.fields.maxResponseSizeMb.label", "Max response size"),
								)}
								description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.maxResponseSizeMb.min}–${bounds.maxResponseSizeMb.max} MB.`}
								suffix=" MB"
								min={bounds.maxResponseSizeMb.min}
								max={bounds.maxResponseSizeMb.max}
								clampBehavior="none"
								allowDecimal={false}
								value={form.maxResponseSizeMb}
								onChange={(value) => onChange("maxResponseSizeMb", value)}
								error={nodeSettingsFieldError(t, errors, "maxResponseSizeMb")}
								data-testid="node-settings-max-response-size"
							/>
						</Stack>
					</Card>
				</>
			);
		case "voice":
			// The voice card itself is composed by the page (it belongs to the voice feature).
			return <NodeSettingsTranscriptionCard {...tunable} />;
		default:
			// Integrations has no draft-bound field; the page composes it alone.
			return null;
	}
}

function LocalChatCard({ form, errors, onChange, autoEffortFastModelOptions }: NodeSettingsFieldsCardProps) {
	const { t } = useTranslation();
	const autoEffortFastOptions = useMemo(() => {
		const options = [
			{ value: "", label: t("pages.nodeSettings.fields.autoEffortFastModel.off", "Off") },
			...autoEffortFastModelOptions,
		];
		// A model that was uninstalled after the setting was saved still has to be selectable, or the select would
		// silently show "Off" for a node that is still configured.
		if (form.autoEffortFastModelName !== "" && !options.some((option) => option.value === form.autoEffortFastModelName)) {
			options.push({ value: form.autoEffortFastModelName, label: form.autoEffortFastModelName });
		}
		return options;
	}, [autoEffortFastModelOptions, form.autoEffortFastModelName, t]);

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-local-chat-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Title order={2} size="h4">
						{t("pages.nodeSettings.fields.localChat.title", "Local chat")}
					</Title>
					<IconRobot size={20} />
				</Group>
				<TextInput
					label={nodeSettingsFieldLabel(
						t,
						"defaultModelName",
						t("pages.nodeSettings.fields.defaultModelName.label", "Default model"),
					)}
					description={t(
						"pages.nodeSettings.fields.defaultModelName.description",
						"The model used for local chat when none is selected. Leave blank to use the configured default.",
					)}
					value={form.defaultModelName}
					onChange={(event) => onChange("defaultModelName", event.currentTarget.value)}
					data-testid="node-settings-default-model"
				/>
				<Switch
					label={t("pages.nodeSettings.fields.enableTools.label", "Enable tools")}
					description={t(
						"pages.nodeSettings.fields.enableTools.description",
						"Allow local chat agents to call tools. Changes take effect without restarting the node.",
					)}
					checked={form.enableTools}
					onChange={(event) => onChange("enableTools", event.currentTarget.checked)}
					data-testid="node-settings-enable-tools"
				/>
				<Switch
					label={t("pages.nodeSettings.fields.customToolsEnabled.label", "Enable custom tools")}
					description={t(
						"pages.nodeSettings.fields.customToolsEnabled.description",
						"Allows agents to run user-defined tools that execute host commands, launch programs, and make network requests. Off by default. Each call still requires your approval.",
					)}
					checked={form.customToolsEnabled}
					onChange={(event) => onChange("customToolsEnabled", event.currentTarget.checked)}
					data-testid="node-settings-custom-tools-enabled"
				/>
				<Switch
					label={t("pages.nodeSettings.fields.toolRelevanceEnabled.label", "Filter tools by relevance")}
					description={t(
						"pages.nodeSettings.fields.toolRelevanceEnabled.description",
						"Send only the tools most relevant to each message when an agent has many. The assistant can still call list_tools to reach the rest. Off by default.",
					)}
					checked={form.toolRelevanceEnabled}
					onChange={(event) => onChange("toolRelevanceEnabled", event.currentTarget.checked)}
					data-testid="node-settings-tool-relevance-enabled"
				/>
				<TagsInput
					label={t("pages.nodeSettings.fields.toolCapableModels.label", "Tool-capable models")}
					description={t(
						"pages.nodeSettings.fields.toolCapableModels.description",
						"Model names that support tool calling. Press Enter to add each name. Changes take effect without restarting the node.",
					)}
					value={form.toolCapableModels}
					onChange={(value) => onChange("toolCapableModels", value)}
					error={nodeSettingsFieldError(t, errors, "toolCapableModels")}
					clearable={true}
					data-testid="node-settings-tool-capable-models"
				/>
				<Select
					label={t("pages.nodeSettings.fields.autoEffortFastModel.label", "Fast model for automatic reasoning effort")}
					description={t(
						"pages.nodeSettings.fields.autoEffortFastModel.description",
						"When a chat turn uses the automatic reasoning effort and the turn looks trivial, run it on this small llama.cpp model instead. Leave off to keep the conversation's own model and only lower the effort. Needs a second loaded-process slot; changes apply to the next message.",
					)}
					data={autoEffortFastOptions}
					value={form.autoEffortFastModelName}
					onChange={(value) => onChange("autoEffortFastModelName", value ?? "")}
					allowDeselect={false}
					searchable={true}
					nothingFoundMessage={t("pages.nodeSettings.fields.autoEffortFastModel.empty", "No installed llama.cpp chat models")}
					error={nodeSettingsFieldError(t, errors, "autoEffortFastModelName")}
					data-testid="node-settings-auto-effort-fast-model"
				/>
			</Stack>
		</Card>
	);
}

const backgroundModelFields = [
	{
		field: "playbookAnalysisModelName",
		label: ["pages.nodeSettings.fields.playbookAnalysisModelName.label", "Playbook analysis model"],
		description: [
			"pages.nodeSettings.fields.playbookAnalysisModelName.description",
			"Reads feedback and proposes playbook actions for review.",
		],
		testId: "node-settings-playbook-analysis-model",
	},
	{
		field: "playbookEvalModelName",
		label: ["pages.nodeSettings.fields.playbookEvalModelName.label", "Playbook eval model"],
		description: [
			"pages.nodeSettings.fields.playbookEvalModelName.description",
			"Re-runs golden conversations to check a suggested action before it is promoted.",
		],
		testId: "node-settings-playbook-eval-model",
	},
	{
		field: "memoryExtractionModelName",
		label: ["pages.nodeSettings.fields.memoryExtractionModelName.label", "Memory extraction model"],
		description: [
			"pages.nodeSettings.fields.memoryExtractionModelName.description",
			"Mines lessons from finished agent runs as memory candidates for review.",
		],
		testId: "node-settings-memory-extraction-model",
	},
] as const;

// The node-local models behind the adaptive-memory background runs. Blank inherits the default model; the server refuses
// anything that is not an installed GGUF or Ollama model on this machine.
function BackgroundModelsCard({ form, errors, onChange, backgroundModelOptions }: NodeSettingsFieldsCardProps) {
	const { t } = useTranslation();
	const inheritLabel = t("pages.nodeSettings.fields.backgroundModels.inherit", "Use the default model");
	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-background-models-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Title order={2} size="h4">
						{t("pages.nodeSettings.fields.backgroundModels.title", "Learning models")}
					</Title>
					<IconBrain size={20} />
				</Group>
				{backgroundModelFields.map(({ field, label, description, testId }) => {
					const value = form[field];
					// A model uninstalled after it was saved stays selectable, or the select would silently show "inherit".
					const data = [
						{ value: "", label: inheritLabel },
						...backgroundModelOptions,
						...(value !== "" && !backgroundModelOptions.some((option) => option.value === value)
							? [{ value, label: value }]
							: []),
					];
					return (
						<Select
							key={field}
							label={t(label[0], label[1])}
							description={t(description[0], description[1])}
							data={data}
							value={value}
							onChange={(next) => onChange(field, next ?? "")}
							allowDeselect={false}
							searchable={true}
							nothingFoundMessage={t("pages.nodeSettings.fields.backgroundModels.empty", "No installed local chat models")}
							error={nodeSettingsFieldError(t, errors, field)}
							data-testid={testId}
						/>
					);
				})}
			</Stack>
		</Card>
	);
}
