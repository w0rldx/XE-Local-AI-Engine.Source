import { Card, Group, NumberInput, Stack, Switch, TagsInput, TextInput, Title } from "@mantine/core";
import { IconRobot, IconServer, IconTool } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { NodeSettingsAdvancedFieldsCard } from "@/features/node-settings/components/NodeSettingsAdvancedFieldsCard";
import {
	nodeSettingsFieldError,
	nodeSettingsRestartHint,
} from "@/features/node-settings/components/NodeSettingsFieldPresentation";
import { NodeSettingsKnowledgeModelsCard } from "@/features/node-settings/components/NodeSettingsKnowledgeModelsCard";
import { NodeSettingsRuntimeCard } from "@/features/node-settings/components/NodeSettingsRuntimeCard";
import { NodeSettingsUsageRatesCard } from "@/features/node-settings/components/NodeSettingsUsageRatesCard";
import type { NodeSettingsFieldBounds, NodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";

// A chat-capable installed model offered as a draft-model choice (value = model name, resolved server-side to a path).
export interface DraftModelOption {
	readonly value: string;
	readonly label: string;
}

// Presentational card group for the migrated appsettings knobs. The page owns the form state, bounds, errors and the
// change handlers; this component only renders the controls. Always-shown fields live in the first three cards; the
// developer-only card is rendered ONLY when the page passes a non-null `developerSection` (so an off-mode save can
// never touch a hidden field — they are not even mounted).
export interface NodeSettingsFieldsCardProps {
	readonly form: NodeSettingsFieldsForm;
	readonly bounds: NodeSettingsFieldBounds;
	readonly errors: Readonly<Record<string, string>>;
	readonly onChange: <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]) => void;
	// When true, the developer-only advanced card is rendered. Driven by the page's developer-mode flag.
	readonly showDeveloperFields: boolean;
	// Installed chat-capable models offered as the draft model for draft-* speculative modes.
	readonly draftModelOptions: readonly DraftModelOption[];
	// Installed llama.cpp chat models eligible for the supervised keep-warm loop.
	readonly keepWarmModelOptions: readonly DraftModelOption[];
	readonly autoEffortFastModelOptions: readonly DraftModelOption[];
	// All installed models offered as the knowledge-base reranker (reranker GGUFs are not a chat kind, so this list is
	// not filtered to chat-capable models).
	readonly rerankerModelOptions: readonly DraftModelOption[];
	// One-click download of the node's recommended reranker GGUF. The page owns the mutation + progress feed; this
	// component only renders the button and reflects its pending / in-flight state.
	readonly onDownloadRecommendedReranker: () => void;
	// True while the download-recommended mutation request is in flight (button shows a spinner).
	readonly isDownloadRecommendedRerankerPending: boolean;
	// True while the recommended reranker's GGUF download is running (duplicate-guards the button after the request
	// returns, until the download reaches a terminal phase).
	readonly isRecommendedRerankerInFlight: boolean;
	// One-click download of the node's recommended embedding GGUF. Unlike the reranker, the embedding model is not a
	// node-settings field (nothing to select/save) — the knowledge base just needs one installed to index documents.
	readonly onDownloadRecommendedEmbedding: () => void;
	// True while the download-recommended embedding mutation request is in flight (button shows a spinner).
	readonly isDownloadRecommendedEmbeddingPending: boolean;
	// True while the recommended embedding model's GGUF download is running (duplicate-guards the button after the
	// request returns, until the download reaches a terminal phase).
	readonly isRecommendedEmbeddingInFlight: boolean;
}

export function NodeSettingsFieldsCard({
	form,
	bounds,
	errors,
	onChange,
	showDeveloperFields,
	draftModelOptions,
	keepWarmModelOptions,
	autoEffortFastModelOptions,
	rerankerModelOptions,
	onDownloadRecommendedReranker,
	isDownloadRecommendedRerankerPending,
	isRecommendedRerankerInFlight,
	onDownloadRecommendedEmbedding,
	isDownloadRecommendedEmbeddingPending,
	isRecommendedEmbeddingInFlight,
}: NodeSettingsFieldsCardProps) {
	const { t } = useTranslation();

	return (
		<>
			<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-local-chat-card">
				<Stack gap="md">
					<Group justify="space-between" align="center">
						<Title order={4}>{t("pages.nodeSettings.fields.localChat.title", "Local chat")}</Title>
						<IconRobot size={20} />
					</Group>
					<TextInput
						label={t("pages.nodeSettings.fields.defaultModelName.label", "Default model")}
						description={
							<>
								{t(
									"pages.nodeSettings.fields.defaultModelName.description",
									"The model used for local chat when none is selected. Leave blank to use the configured default.",
								)}
								{nodeSettingsRestartHint(t, "defaultModelName")}
							</>
						}
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
				</Stack>
			</Card>

			<NodeSettingsRuntimeCard
				form={form}
				bounds={bounds}
				errors={errors}
				onChange={onChange}
				draftModelOptions={draftModelOptions}
				keepWarmModelOptions={keepWarmModelOptions}
				autoEffortFastModelOptions={autoEffortFastModelOptions}
			/>

			<NodeSettingsKnowledgeModelsCard
				form={form}
				errors={errors}
				onChange={onChange}
				rerankerModelOptions={rerankerModelOptions}
				rerankerDownload={{
					onStart: onDownloadRecommendedReranker,
					pending: isDownloadRecommendedRerankerPending,
					inFlight: isRecommendedRerankerInFlight,
				}}
				embeddingDownload={{
					onStart: onDownloadRecommendedEmbedding,
					pending: isDownloadRecommendedEmbeddingPending,
					inFlight: isRecommendedEmbeddingInFlight,
				}}
			/>

			<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-hf-card">
				<Stack gap="md">
					<Group justify="space-between" align="center">
						<Title order={4}>{t("pages.nodeSettings.fields.huggingFace.title", "Hugging Face")}</Title>
						<IconServer size={20} />
					</Group>
					<TextInput
						label={t("pages.nodeSettings.fields.huggingFaceDefaultQuant.label", "Default quantization")}
						description={
							<>
								{t(
									"pages.nodeSettings.fields.huggingFaceDefaultQuant.description",
									"Preferred GGUF quantization when downloading from Hugging Face (e.g. Q4_K_M).",
								)}
								{nodeSettingsRestartHint(t, "huggingFaceDefaultQuant")}
							</>
						}
						value={form.huggingFaceDefaultQuant}
						onChange={(event) => onChange("huggingFaceDefaultQuant", event.currentTarget.value)}
						data-testid="node-settings-hf-default-quant"
					/>
				</Stack>
			</Card>

			<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-worker-card">
				<Stack gap="md">
					<Group justify="space-between" align="center">
						<Title order={4}>{t("pages.nodeSettings.fields.worker.title", "Worker limits")}</Title>
						<IconTool size={20} />
					</Group>
					<NumberInput
						label={t("pages.nodeSettings.fields.maxResponseSizeMb.label", "Max response size")}
						description={
							<>
								{`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.maxResponseSizeMb.min}–${bounds.maxResponseSizeMb.max} MB.`}
								{nodeSettingsRestartHint(t, "maxResponseSizeMb")}
							</>
						}
						suffix=" MB"
						min={bounds.maxResponseSizeMb.min}
						max={bounds.maxResponseSizeMb.max}
						allowDecimal={false}
						value={form.maxResponseSizeMb}
						onChange={(value) => onChange("maxResponseSizeMb", value)}
						error={nodeSettingsFieldError(t, errors, "maxResponseSizeMb")}
						data-testid="node-settings-max-response-size"
					/>
				</Stack>
			</Card>

			<NodeSettingsUsageRatesCard
				usageRates={form.usageRates}
				error={nodeSettingsFieldError(t, errors, "usageRates")}
				onChange={(usageRates) => onChange("usageRates", usageRates)}
			/>

			{showDeveloperFields ? (
				<NodeSettingsAdvancedFieldsCard form={form} bounds={bounds} errors={errors} onChange={onChange} />
			) : null}
		</>
	);
}
