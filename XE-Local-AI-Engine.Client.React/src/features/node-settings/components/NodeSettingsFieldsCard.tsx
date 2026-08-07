import {
	ActionIcon,
	Button,
	Card,
	Group,
	NumberInput,
	Select,
	Stack,
	Switch,
	TagsInput,
	Text,
	TextInput,
	Title,
} from "@mantine/core";
import { IconCloudDownload, IconCoin, IconCpu, IconPlus, IconRobot, IconServer, IconTool, IconTrash } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import {
	type NodeSettingsFieldBounds,
	type NodeSettingsFieldsForm,
	newUsageRateRow,
	requiresExternalDraftModel,
	SPECULATIVE_DISABLED_MODE,
	speculativeModeSelectValues,
	type UsageRateRow,
	usesDraftTokensPerStep,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";

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

function fieldError(
	t: ReturnType<typeof useTranslation>["t"],
	errors: Readonly<Record<string, string>>,
	field: string,
): string | undefined {
	const code = errors[field];
	if (code === undefined) {
		return undefined;
	}
	return t(`pages.nodeSettings.fields.errors.${code}`, "Invalid value.");
}

export function NodeSettingsFieldsCard({
	form,
	bounds,
	errors,
	onChange,
	showDeveloperFields,
	draftModelOptions,
	keepWarmModelOptions,
	rerankerModelOptions,
	onDownloadRecommendedReranker,
	isDownloadRecommendedRerankerPending,
	isRecommendedRerankerInFlight,
	onDownloadRecommendedEmbedding,
	isDownloadRecommendedEmbeddingPending,
	isRecommendedEmbeddingInFlight,
}: NodeSettingsFieldsCardProps) {
	const { t } = useTranslation();

	// Curated mode options with i18n labels; `none` renders as "Off". Kept as data so the Select stays declarative.
	const speculativeModeOptions = useMemo(
		() =>
			speculativeModeSelectValues.map((mode) => ({
				value: mode,
				label:
					mode === SPECULATIVE_DISABLED_MODE
						? t("pages.nodeSettings.fields.speculativeMode.options.off", "Off")
						: t(`pages.nodeSettings.fields.speculativeMode.options.${mode}`, mode),
			})),
		[t],
	);

	// The draft-model picker is only meaningful for modes that load a second GGUF; draft-mtp drafts from the main model's
	// own heads, so it shows the tokens-per-step knob without the picker.
	const needsDraftModel = requiresExternalDraftModel(form.speculativeMode);
	const showsDraftTokensPerStep = usesDraftTokensPerStep(form.speculativeMode);

	// Usage-rate row editing: every mutation replaces the whole array via the generic onChange so the page's single
	// form-state reducer stays the source of truth (no local row state to drift).
	const addRateRow = (): void => onChange("usageRates", [...form.usageRates, newUsageRateRow()]);
	const updateRateRow = (id: string, patch: Partial<Omit<UsageRateRow, "id">>): void =>
		onChange(
			"usageRates",
			form.usageRates.map((row) => (row.id === id ? { ...row, ...patch } : row)),
		);
	const removeRateRow = (id: string): void =>
		onChange(
			"usageRates",
			form.usageRates.filter((row) => row.id !== id),
		);
	const usageRatesError = fieldError(t, errors, "usageRates");

	// Reranker options: an explicit "Off" entry (empty value = reranking disabled) followed by every installed model.
	// If a reranker model is stored but no longer installed (or not in the returned list), keep it selectable so the
	// operator sees their current value instead of a blank control.
	const rerankerOptions = useMemo(() => {
		const options = [
			{ value: "", label: t("pages.nodeSettings.fields.rerankerModel.off", "Off") },
			...rerankerModelOptions.map((option) => ({ value: option.value, label: option.label })),
		];
		if (form.rerankerModelName !== "" && !options.some((option) => option.value === form.rerankerModelName)) {
			options.push({ value: form.rerankerModelName, label: form.rerankerModelName });
		}
		return options;
	}, [rerankerModelOptions, form.rerankerModelName, t]);

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
						description={t(
							"pages.nodeSettings.fields.defaultModelName.description",
							"The model used for local chat when none is selected. Leave blank to use the configured default. Applies after the node restarts.",
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
					<TagsInput
						label={t("pages.nodeSettings.fields.toolCapableModels.label", "Tool-capable models")}
						description={t(
							"pages.nodeSettings.fields.toolCapableModels.description",
							"Model names that support tool calling. Press Enter to add each name. Changes take effect without restarting the node.",
						)}
						value={form.toolCapableModels}
						onChange={(value) => onChange("toolCapableModels", value)}
						error={fieldError(t, errors, "toolCapableModels")}
						clearable={true}
						data-testid="node-settings-tool-capable-models"
					/>
				</Stack>
			</Card>

			<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-runtime-card">
				<Stack gap="md">
					<Group justify="space-between" align="center">
						<Title order={4}>{t("pages.nodeSettings.fields.runtime.title", "Local model runtime")}</Title>
						<IconCpu size={20} />
					</Group>
					<NumberInput
						label={t("pages.nodeSettings.fields.llamaMaxLoadedProcesses.label", "Max loaded llama-server processes")}
						description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.llamaMaxLoadedProcesses.min}–${bounds.llamaMaxLoadedProcesses.max}. ${t("pages.nodeSettings.fields.llamaMaxLoadedProcesses.description", "Applies after the node restarts.")}`}
						min={bounds.llamaMaxLoadedProcesses.min}
						max={bounds.llamaMaxLoadedProcesses.max}
						allowDecimal={false}
						value={form.llamaMaxLoadedProcesses}
						onChange={(value) => onChange("llamaMaxLoadedProcesses", value)}
						error={fieldError(t, errors, "llamaMaxLoadedProcesses")}
						data-testid="node-settings-llama-max-processes"
					/>
					<NumberInput
						label={t("pages.nodeSettings.fields.llamaIdleTimeToLiveSeconds.label", "Idle process time-to-live")}
						description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.llamaIdleTimeToLiveSeconds.min}–${bounds.llamaIdleTimeToLiveSeconds.max} ${t("pages.nodeSettings.fields.seconds", "seconds")}. ${t("pages.nodeSettings.fields.llamaIdleTimeToLiveSeconds.description", "Applies after the node restarts.")}`}
						suffix={` ${t("pages.nodeSettings.fields.seconds", "seconds")}`}
						min={bounds.llamaIdleTimeToLiveSeconds.min}
						max={bounds.llamaIdleTimeToLiveSeconds.max}
						allowDecimal={false}
						value={form.llamaIdleTimeToLiveSeconds}
						onChange={(value) => onChange("llamaIdleTimeToLiveSeconds", value)}
						error={fieldError(t, errors, "llamaIdleTimeToLiveSeconds")}
						data-testid="node-settings-llama-idle-ttl"
					/>
					<Switch
						label={t("pages.nodeSettings.fields.keepModelWarm.enabledLabel", "Keep a model warm")}
						description={t(
							"pages.nodeSettings.fields.keepModelWarm.enabledDescription",
							"Continuously keeps one selected llama.cpp chat model resident. Changes take effect without restarting the node.",
						)}
						checked={form.keepModelWarmEnabled}
						onChange={(event) => onChange("keepModelWarmEnabled", event.currentTarget.checked)}
						data-testid="node-settings-keep-model-warm-enabled"
					/>
					<Select
						label={t("pages.nodeSettings.fields.keepModelWarm.modelLabel", "Model to keep warm")}
						description={t(
							"pages.nodeSettings.fields.keepModelWarm.modelDescription",
							"Choose an installed llama.cpp chat model.",
						)}
						placeholder={t("pages.nodeSettings.fields.keepModelWarm.modelPlaceholder", "Select a model")}
						data={[...keepWarmModelOptions]}
						value={form.keepModelWarmModelName === "" ? null : form.keepModelWarmModelName}
						onChange={(value) => onChange("keepModelWarmModelName", value ?? "")}
						disabled={!form.keepModelWarmEnabled}
						searchable={true}
						nothingFoundMessage={t("pages.nodeSettings.fields.keepModelWarm.noModels", "No installed llama.cpp chat models")}
						error={
							errors["keepModelWarmModelName"] === "unavailableKeepWarmModel"
								? t(
										"pages.nodeSettings.fields.errors.unavailableKeepWarmModel",
										"The selected model {{model}} is no longer installed.",
										{ model: form.keepModelWarmModelName },
									)
								: fieldError(t, errors, "keepModelWarmModelName")
						}
						data-testid="node-settings-keep-model-warm-model"
					/>
					<NumberInput
						label={t("pages.nodeSettings.fields.keepModelWarm.intervalLabel", "Warm interval")}
						description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.keepModelWarmIntervalSeconds.min}–${bounds.keepModelWarmIntervalSeconds.max} ${t("pages.nodeSettings.fields.seconds", "seconds")}.`}
						suffix={` ${t("pages.nodeSettings.fields.seconds", "seconds")}`}
						min={bounds.keepModelWarmIntervalSeconds.min}
						max={bounds.keepModelWarmIntervalSeconds.max}
						allowDecimal={false}
						value={form.keepModelWarmIntervalSeconds}
						onChange={(value) => onChange("keepModelWarmIntervalSeconds", value)}
						disabled={!form.keepModelWarmEnabled}
						error={fieldError(t, errors, "keepModelWarmIntervalSeconds")}
						data-testid="node-settings-keep-model-warm-interval"
					/>
					<Text size="xs" c="dimmed" data-testid="node-settings-keep-model-warm-help">
						{t(
							"pages.nodeSettings.fields.keepModelWarm.help",
							"Pinning keeps VRAM occupied and permanently uses one of the configured {{maxLoadedProcesses}} MaxLoadedProcesses slots. The warm interval must remain below the idle TTL to prevent eviction.",
							{ maxLoadedProcesses: form.llamaMaxLoadedProcesses },
						)}
					</Text>
					<TextInput
						label={t("pages.nodeSettings.fields.ollamaEndpoint.label", "Ollama endpoint")}
						description={t(
							"pages.nodeSettings.fields.ollamaEndpoint.description",
							"The Ollama API base URL. Applies after a restart.",
						)}
						placeholder="http://127.0.0.1:11434"
						value={form.ollamaEndpoint}
						onChange={(event) => onChange("ollamaEndpoint", event.currentTarget.value)}
						error={fieldError(t, errors, "ollamaEndpoint")}
						data-testid="node-settings-ollama-endpoint"
					/>
					<Text size="xs" c="dimmed">
						{t(
							"pages.nodeSettings.fields.runtime.tagHint",
							"The recommended llama.cpp version is managed in the llama.cpp runtime updates card above.",
						)}
					</Text>
					<Select
						label={t("pages.nodeSettings.fields.speculativeMode.label", "Speculative decoding")}
						description={t(
							"pages.nodeSettings.fields.speculativeMode.description",
							"Draft-and-verify decoding raises single-user throughput. n-gram modes need no extra model; draft models use additional VRAM not yet counted by capacity checks. Applies after the node restarts.",
						)}
						data={speculativeModeOptions}
						value={form.speculativeMode}
						onChange={(value) => onChange("speculativeMode", value ?? SPECULATIVE_DISABLED_MODE)}
						allowDeselect={false}
						error={fieldError(t, errors, "speculativeMode")}
						data-testid="node-settings-speculative-mode"
					/>
					{needsDraftModel ? (
						<Select
							label={t("pages.nodeSettings.fields.speculativeDraftModel.label", "Draft model")}
							description={t(
								"pages.nodeSettings.fields.speculativeDraftModel.description",
								"An installed chat-capable model used as the drafter. Must share the target model's tokenizer family. Applies after the node restarts.",
							)}
							placeholder={t("pages.nodeSettings.fields.speculativeDraftModel.placeholder", "Select a draft model")}
							data={[...draftModelOptions]}
							value={form.speculativeDraftModelName === "" ? null : form.speculativeDraftModelName}
							onChange={(value) => onChange("speculativeDraftModelName", value ?? "")}
							searchable={true}
							nothingFoundMessage={t("pages.nodeSettings.fields.speculativeDraftModel.empty", "No installed chat models")}
							error={fieldError(t, errors, "speculativeDraftModelName")}
							data-testid="node-settings-speculative-draft-model"
						/>
					) : null}
					{showsDraftTokensPerStep ? (
						<NumberInput
							label={t("pages.nodeSettings.fields.speculativeDraftMaxTokens.label", "Draft tokens per step")}
							description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.speculativeDraftMaxTokens.min}–${bounds.speculativeDraftMaxTokens.max}. ${t("pages.nodeSettings.fields.speculativeDraftMaxTokens.description", "Applies after the node restarts.")}`}
							min={bounds.speculativeDraftMaxTokens.min}
							max={bounds.speculativeDraftMaxTokens.max}
							allowDecimal={false}
							value={form.speculativeDraftMaxTokens}
							onChange={(value) => onChange("speculativeDraftMaxTokens", value)}
							error={fieldError(t, errors, "speculativeDraftMaxTokens")}
							data-testid="node-settings-speculative-draft-max-tokens"
						/>
					) : null}
					<NumberInput
						label={t("pages.nodeSettings.fields.chatCacheReuse.label", "Prompt cache reuse")}
						description={t(
							"pages.nodeSettings.fields.chatCacheReuse.description",
							"Reuse an unchanged prompt prefix across turns (tokens). 0 disables. Applies after the node restarts.",
						)}
						min={bounds.chatCacheReuse.min}
						max={bounds.chatCacheReuse.max}
						allowDecimal={false}
						value={form.chatCacheReuse}
						onChange={(value) => onChange("chatCacheReuse", value)}
						error={fieldError(t, errors, "chatCacheReuse")}
						data-testid="node-settings-chat-cache-reuse"
					/>
					<Group justify="space-between" align="center" wrap="nowrap" gap="md">
						<Text size="xs" c="dimmed">
							{t(
								"pages.nodeSettings.fields.embeddingModel.recommendedHelp",
								"Required for the knowledge base: without an embedding model, documents cannot be indexed. Recommended: nomic-embed-text-v1.5 (~274 MB).",
							)}
						</Text>
						<Button
							variant="light"
							size="xs"
							leftSection={<IconCloudDownload size={14} />}
							onClick={onDownloadRecommendedEmbedding}
							loading={isDownloadRecommendedEmbeddingPending}
							disabled={isDownloadRecommendedEmbeddingPending || isRecommendedEmbeddingInFlight}
							data-testid="node-settings-embedding-download-recommended"
						>
							{t("pages.nodeSettings.fields.embeddingModel.downloadRecommended", "Download recommended embedding model")}
						</Button>
					</Group>
					<Select
						label={t("pages.nodeSettings.fields.rerankerModel.label", "Reranker model")}
						description={t(
							"pages.nodeSettings.fields.rerankerModel.description",
							"Cross-encoder reranker that reorders knowledge-base search results for relevance. Leave off if no reranker model is installed. Applies after the node restarts. Uses additional VRAM not counted by capacity checks.",
						)}
						data={rerankerOptions}
						value={form.rerankerModelName}
						onChange={(value) => onChange("rerankerModelName", value ?? "")}
						allowDeselect={false}
						searchable={true}
						nothingFoundMessage={t("pages.nodeSettings.fields.rerankerModel.empty", "No installed models")}
						error={fieldError(t, errors, "rerankerModelName")}
						data-testid="node-settings-reranker-model"
					/>
					<Group justify="space-between" align="center" wrap="nowrap" gap="md">
						<Text size="xs" c="dimmed">
							{t(
								"pages.nodeSettings.fields.rerankerModel.recommendedHelp",
								"Recommended: bge-reranker-v2-m3, which runs as its own extra model server.",
							)}
						</Text>
						<Button
							variant="light"
							size="xs"
							leftSection={<IconCloudDownload size={14} />}
							onClick={onDownloadRecommendedReranker}
							loading={isDownloadRecommendedRerankerPending}
							disabled={isDownloadRecommendedRerankerPending || isRecommendedRerankerInFlight}
							data-testid="node-settings-reranker-download-recommended"
						>
							{t("pages.nodeSettings.fields.rerankerModel.downloadRecommended", "Download recommended reranker")}
						</Button>
					</Group>
				</Stack>
			</Card>

			<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-hf-card">
				<Stack gap="md">
					<Group justify="space-between" align="center">
						<Title order={4}>{t("pages.nodeSettings.fields.huggingFace.title", "Hugging Face")}</Title>
						<IconServer size={20} />
					</Group>
					<TextInput
						label={t("pages.nodeSettings.fields.huggingFaceDefaultQuant.label", "Default quantization")}
						description={t(
							"pages.nodeSettings.fields.huggingFaceDefaultQuant.description",
							"Preferred GGUF quantization when downloading from Hugging Face (e.g. Q4_K_M). Applies after the node restarts.",
						)}
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
						description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.maxResponseSizeMb.min}–${bounds.maxResponseSizeMb.max} MB. ${t("pages.nodeSettings.fields.maxResponseSizeMb.description", "Applies after the node restarts.")}`}
						suffix=" MB"
						min={bounds.maxResponseSizeMb.min}
						max={bounds.maxResponseSizeMb.max}
						allowDecimal={false}
						value={form.maxResponseSizeMb}
						onChange={(value) => onChange("maxResponseSizeMb", value)}
						error={fieldError(t, errors, "maxResponseSizeMb")}
						data-testid="node-settings-max-response-size"
					/>
				</Stack>
			</Card>

			<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-usage-rates-card">
				<Stack gap="md">
					<Group justify="space-between" align="center">
						<Title order={4}>{t("pages.nodeSettings.fields.usageRates.title", "Usage cost rates")}</Title>
						<IconCoin size={20} />
					</Group>
					<Text size="sm" c="dimmed">
						{t(
							"pages.nodeSettings.fields.usageRates.description",
							"Approximate per-model cost rates (USD per 1M tokens) used to estimate cost on the Usage dashboard. Local and unpriced models are treated as free. These are estimates in your operator currency (USD), not a bill.",
						)}
					</Text>
					{form.usageRates.length === 0 ? (
						<Text size="sm" c="dimmed" data-testid="node-settings-usage-rates-empty">
							{t("pages.nodeSettings.fields.usageRates.empty", "No rates configured. Add a rate to estimate cost for a model.")}
						</Text>
					) : (
						<Stack gap="xs">
							<Group gap="sm" wrap="nowrap" visibleFrom="sm">
								<Text size="xs" fw={600} c="dimmed" style={{ flex: 1 }}>
									{t("pages.nodeSettings.fields.usageRates.columns.model", "Model name")}
								</Text>
								<Text size="xs" fw={600} c="dimmed" style={{ width: 140 }}>
									{t("pages.nodeSettings.fields.usageRates.columns.input", "Input $/1M")}
								</Text>
								<Text size="xs" fw={600} c="dimmed" style={{ width: 140 }}>
									{t("pages.nodeSettings.fields.usageRates.columns.output", "Output $/1M")}
								</Text>
								<div style={{ width: 36 }} />
							</Group>
							{form.usageRates.map((row) => (
								<Group key={row.id} gap="sm" wrap="nowrap" align="flex-start" data-testid="node-settings-usage-rate-row">
									<TextInput
										aria-label={t("pages.nodeSettings.fields.usageRates.columns.model", "Model name")}
										placeholder={t("pages.nodeSettings.fields.usageRates.modelPlaceholder", "e.g. gpt-5")}
										value={row.modelName}
										onChange={(event) => updateRateRow(row.id, { modelName: event.currentTarget.value })}
										style={{ flex: 1 }}
										data-testid="node-settings-usage-rate-model"
									/>
									<NumberInput
										aria-label={t("pages.nodeSettings.fields.usageRates.columns.input", "Input $/1M")}
										min={0}
										step={0.5}
										decimalScale={4}
										value={row.inputPer1M}
										onChange={(value) => updateRateRow(row.id, { inputPer1M: value })}
										style={{ width: 140 }}
										data-testid="node-settings-usage-rate-input"
									/>
									<NumberInput
										aria-label={t("pages.nodeSettings.fields.usageRates.columns.output", "Output $/1M")}
										min={0}
										step={0.5}
										decimalScale={4}
										value={row.outputPer1M}
										onChange={(value) => updateRateRow(row.id, { outputPer1M: value })}
										style={{ width: 140 }}
										data-testid="node-settings-usage-rate-output"
									/>
									<ActionIcon
										variant="subtle"
										color="red"
										aria-label={t("pages.nodeSettings.fields.usageRates.remove", "Remove rate")}
										onClick={() => removeRateRow(row.id)}
										data-testid="node-settings-usage-rate-remove"
									>
										<IconTrash size={16} />
									</ActionIcon>
								</Group>
							))}
						</Stack>
					)}
					{usageRatesError ? (
						<Text size="sm" c="red" data-testid="node-settings-usage-rates-error">
							{usageRatesError}
						</Text>
					) : null}
					<Group>
						<Button
							variant="light"
							size="xs"
							leftSection={<IconPlus size={14} />}
							onClick={addRateRow}
							data-testid="node-settings-usage-rate-add"
						>
							{t("pages.nodeSettings.fields.usageRates.add", "Add rate")}
						</Button>
					</Group>
				</Stack>
			</Card>

			{showDeveloperFields ? (
				<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-advanced-card">
					<Stack gap="md">
						<Group justify="space-between" align="center">
							<Title order={4}>{t("pages.nodeSettings.fields.advanced.title", "Advanced (developer)")}</Title>
							<IconTool size={20} />
						</Group>
						<Text c="dimmed" size="sm">
							{t(
								"pages.nodeSettings.fields.advanced.description",
								"Low-level orchestration and AgentHome limits. The orchestration idle timeout and pending tool-call age apply after a restart; the AgentHome limits apply immediately.",
							)}
						</Text>
						<NumberInput
							label={t("pages.nodeSettings.fields.orchestrationIdleTimeoutSeconds.label", "Orchestration idle timeout")}
							description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.orchestrationIdleTimeoutSeconds.min}–${bounds.orchestrationIdleTimeoutSeconds.max} ${t("pages.nodeSettings.fields.seconds", "seconds")}. ${t("pages.nodeSettings.fields.orchestrationIdleTimeoutSeconds.description", "Applies after the node restarts.")}`}
							suffix={` ${t("pages.nodeSettings.fields.seconds", "seconds")}`}
							min={bounds.orchestrationIdleTimeoutSeconds.min}
							max={bounds.orchestrationIdleTimeoutSeconds.max}
							allowDecimal={false}
							value={form.orchestrationIdleTimeoutSeconds}
							onChange={(value) => onChange("orchestrationIdleTimeoutSeconds", value)}
							error={fieldError(t, errors, "orchestrationIdleTimeoutSeconds")}
							data-testid="node-settings-orchestration-idle-timeout"
						/>
						<NumberInput
							label={t("pages.nodeSettings.fields.agentHomePrepareTimeoutSeconds.label", "AgentHome prepare timeout")}
							description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.agentHomeTimeoutSeconds.min}–${bounds.agentHomeTimeoutSeconds.max} ${t("pages.nodeSettings.fields.seconds", "seconds")}. ${t("pages.nodeSettings.fields.agentHomePrepareTimeoutSeconds.description", "Changes take effect without restarting the node.")}`}
							suffix={` ${t("pages.nodeSettings.fields.seconds", "seconds")}`}
							min={bounds.agentHomeTimeoutSeconds.min}
							max={bounds.agentHomeTimeoutSeconds.max}
							allowDecimal={false}
							value={form.agentHomePrepareTimeoutSeconds}
							onChange={(value) => onChange("agentHomePrepareTimeoutSeconds", value)}
							error={fieldError(t, errors, "agentHomePrepareTimeoutSeconds")}
							data-testid="node-settings-agenthome-prepare-timeout"
						/>
						<NumberInput
							label={t("pages.nodeSettings.fields.agentHomeCommandTimeoutSeconds.label", "AgentHome command timeout")}
							description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.agentHomeTimeoutSeconds.min}–${bounds.agentHomeTimeoutSeconds.max} ${t("pages.nodeSettings.fields.seconds", "seconds")}. ${t("pages.nodeSettings.fields.agentHomeCommandTimeoutSeconds.description", "Changes take effect without restarting the node.")}`}
							suffix={` ${t("pages.nodeSettings.fields.seconds", "seconds")}`}
							min={bounds.agentHomeTimeoutSeconds.min}
							max={bounds.agentHomeTimeoutSeconds.max}
							allowDecimal={false}
							value={form.agentHomeCommandTimeoutSeconds}
							onChange={(value) => onChange("agentHomeCommandTimeoutSeconds", value)}
							error={fieldError(t, errors, "agentHomeCommandTimeoutSeconds")}
							data-testid="node-settings-agenthome-command-timeout"
						/>
						<NumberInput
							label={t("pages.nodeSettings.fields.agentHomeMaxSelectedFolderBytes.label", "AgentHome max selected folder size")}
							description={t(
								"pages.nodeSettings.fields.bytesPositive",
								"A positive number of bytes. Changes take effect without restarting the node.",
							)}
							suffix=" bytes"
							min={1}
							allowDecimal={false}
							value={form.agentHomeMaxSelectedFolderBytes}
							onChange={(value) => onChange("agentHomeMaxSelectedFolderBytes", value)}
							error={fieldError(t, errors, "agentHomeMaxSelectedFolderBytes")}
							data-testid="node-settings-agenthome-max-folder-bytes"
						/>
						<NumberInput
							label={t("pages.nodeSettings.fields.agentHomeMaxPatchBytes.label", "AgentHome max patch size")}
							description={t(
								"pages.nodeSettings.fields.bytesPositive",
								"A positive number of bytes. Changes take effect without restarting the node.",
							)}
							suffix=" bytes"
							min={1}
							allowDecimal={false}
							value={form.agentHomeMaxPatchBytes}
							onChange={(value) => onChange("agentHomeMaxPatchBytes", value)}
							error={fieldError(t, errors, "agentHomeMaxPatchBytes")}
							data-testid="node-settings-agenthome-max-patch-bytes"
						/>
						<NumberInput
							label={t("pages.nodeSettings.fields.maxPendingToolCallAgeMinutes.label", "Max pending tool-call age")}
							description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.maxPendingToolCallAgeMinutes.min}–${bounds.maxPendingToolCallAgeMinutes.max} ${t("pages.nodeSettings.fields.minutes", "minutes")}. ${t("pages.nodeSettings.fields.maxPendingToolCallAgeMinutes.description", "Applies after the node restarts.")}`}
							suffix={` ${t("pages.nodeSettings.fields.minutes", "minutes")}`}
							min={bounds.maxPendingToolCallAgeMinutes.min}
							max={bounds.maxPendingToolCallAgeMinutes.max}
							allowDecimal={false}
							value={form.maxPendingToolCallAgeMinutes}
							onChange={(value) => onChange("maxPendingToolCallAgeMinutes", value)}
							error={fieldError(t, errors, "maxPendingToolCallAgeMinutes")}
							data-testid="node-settings-max-pending-toolcall-age"
						/>
						<Text size="xs" c="dimmed">
							{t(
								"pages.nodeSettings.fields.advanced.samplingNote",
								"Sampling defaults are configured per message during a chat.",
							)}
						</Text>
					</Stack>
				</Card>
			) : null}
		</>
	);
}
