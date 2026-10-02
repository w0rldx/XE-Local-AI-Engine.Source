import { Card, Group, NumberInput, SegmentedControl, Select, Stack, Switch, Text, Title } from "@mantine/core";
import { IconClockHour4, IconCpu, IconFlame, IconGauge, IconRocket } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import {
	nodeSettingsAllowedRange,
	nodeSettingsFieldError,
	nodeSettingsFieldLabel,
} from "@/features/node-settings/components/NodeSettingsFieldPresentation";
import { NodeSettingsNumberField } from "@/features/node-settings/components/NodeSettingsNumberField";
import {
	type ChatCacheRamMode,
	KV_CACHE_TYPE_DEFAULT,
	kvCacheTypeSelectValues,
	nodeSettingsDisplayScale,
	type NodeSettingsFieldBounds,
	type NodeSettingsFieldsForm,
	type NodeSettingsModelOption,
	requiresExternalDraftModel,
	SPECULATIVE_DISABLED_MODE,
	speculativeModeSelectValues,
	toDisplayBounds,
	usesDraftTokensPerStep,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";

interface Props {
	readonly form: NodeSettingsFieldsForm;
	readonly bounds: NodeSettingsFieldBounds;
	readonly errors: Readonly<Record<string, string>>;
	readonly onChange: <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]) => void;
	readonly draftModelOptions: readonly NodeSettingsModelOption[];
	readonly keepWarmModelOptions: readonly NodeSettingsModelOption[];
}

// The "Model runtime" section: the process pool, server timeouts, resource reserve, keep-warm and the chat launch tuning,
// one card each so no card mixes subsystems. Every restart-gated field carries its badge in the label.
export function NodeSettingsRuntimeCard({ form, bounds, errors, onChange, draftModelOptions, keepWarmModelOptions }: Props) {
	const { t } = useTranslation();
	const minutes = t("pages.nodeSettings.fields.minutes", "minutes");
	const seconds = t("pages.nodeSettings.fields.seconds", "seconds");
	const secondsShort = t("pages.nodeSettings.fields.secondsShort", "s");
	const field = { form, errors, onChange };
	const idleTtlBounds = toDisplayBounds(bounds.llamaIdleTimeToLiveSeconds, nodeSettingsDisplayScale.llamaIdleTimeToLiveSeconds);
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
	const kvCacheTypeOptions = useMemo(
		() =>
			kvCacheTypeSelectValues.map((type) => ({
				value: type,
				label: t(`pages.nodeSettings.fields.kvCacheType.options.${type}`, type),
			})),
		[t],
	);
	const needsDraftModel = requiresExternalDraftModel(form.speculativeMode);
	const showsDraftTokensPerStep = usesDraftTokensPerStep(form.speculativeMode);
	return (
		<>
			<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-runtime-card">
				<Stack gap="md">
					<Group justify="space-between" align="center">
						<Title order={2} size="h4">
							{t("pages.nodeSettings.fields.runtime.title", "Process pool")}
						</Title>
						<IconCpu size={20} />
					</Group>
					<NumberInput
						label={nodeSettingsFieldLabel(
							t,
							"llamaMaxLoadedProcesses",
							t("pages.nodeSettings.fields.llamaMaxLoadedProcesses.label", "Max loaded llama-server processes"),
						)}
						description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.llamaMaxLoadedProcesses.min}–${bounds.llamaMaxLoadedProcesses.max}.`}
						min={bounds.llamaMaxLoadedProcesses.min}
						max={bounds.llamaMaxLoadedProcesses.max}
						allowDecimal={false}
						value={form.llamaMaxLoadedProcesses}
						onChange={(value) => onChange("llamaMaxLoadedProcesses", value)}
						error={nodeSettingsFieldError(t, errors, "llamaMaxLoadedProcesses")}
						data-testid="node-settings-llama-max-processes"
					/>
					<NumberInput
						label={nodeSettingsFieldLabel(
							t,
							"llamaIdleTimeToLiveSeconds",
							t("pages.nodeSettings.fields.llamaIdleTimeToLiveSeconds.label", "Idle process time-to-live"),
						)}
						description={nodeSettingsAllowedRange(
							t,
							bounds.llamaIdleTimeToLiveSeconds,
							minutes,
							nodeSettingsDisplayScale.llamaIdleTimeToLiveSeconds,
							t("pages.nodeSettings.fields.secondsShort", "s"),
						)}
						suffix={` ${minutes}`}
						min={idleTtlBounds.min}
						max={idleTtlBounds.max}
						decimalScale={2}
						value={form.llamaIdleTimeToLiveSeconds}
						onChange={(value) => onChange("llamaIdleTimeToLiveSeconds", value)}
						error={nodeSettingsFieldError(t, errors, "llamaIdleTimeToLiveSeconds")}
						data-testid="node-settings-llama-idle-ttl"
					/>
					<NodeSettingsNumberField
						{...field}
						field="imageIdleTimeToLiveSeconds"
						label={t("pages.nodeSettings.fields.imageIdleTimeToLiveSeconds.label", "Image server idle time-to-live")}
						bounds={bounds.tunables.imageIdleTimeToLiveSeconds}
						unit={minutes}
						wireUnit={secondsShort}
						testId="node-settings-image-idle-ttl"
					/>
					<NodeSettingsNumberField
						{...field}
						field="imageMaxLoadedProcesses"
						label={t("pages.nodeSettings.fields.imageMaxLoadedProcesses.label", "Max loaded image servers")}
						description={t(
							"pages.nodeSettings.fields.imageMaxLoadedProcesses.description",
							"How many image models stay loaded at once; loading one more unloads the least recently used.",
						)}
						bounds={bounds.tunables.imageMaxLoadedProcesses}
						testId="node-settings-image-max-processes"
					/>
					<Switch
						label={nodeSettingsFieldLabel(
							t,
							"imageTextEncoderOnGpu",
							t("pages.nodeSettings.fields.imageTextEncoderOnGpu.label", "Image text encoder on the GPU"),
						)}
						description={t(
							"pages.nodeSettings.fields.imageTextEncoderOnGpu.description",
							"Faster prompts at the cost of video memory; off keeps the text encoder on the CPU.",
						)}
						checked={form.imageTextEncoderOnGpu}
						onChange={(event) => onChange("imageTextEncoderOnGpu", event.currentTarget.checked)}
						data-testid="node-settings-image-text-encoder-gpu"
					/>
				</Stack>
			</Card>
			<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-server-timeouts-card">
				<Stack gap="md">
					<Group justify="space-between" align="center">
						<Title order={2} size="h4">
							{t("pages.nodeSettings.fields.serverTimeouts.title", "Server timeouts")}
						</Title>
						<IconClockHour4 size={20} />
					</Group>
					<NodeSettingsNumberField
						{...field}
						field="llamaReadinessTimeoutCapSeconds"
						label={t("pages.nodeSettings.fields.llamaReadinessTimeoutCapSeconds.label", "Model load timeout cap")}
						description={t(
							"pages.nodeSettings.fields.llamaReadinessTimeoutCapSeconds.description",
							"The longest a llama-server may take to load a model before the start is given up.",
						)}
						bounds={bounds.tunables.llamaReadinessTimeoutCapSeconds}
						unit={seconds}
						testId="node-settings-llama-readiness-cap"
					/>
					<NodeSettingsNumberField
						{...field}
						field="llamaChatHttpTimeoutSeconds"
						label={t("pages.nodeSettings.fields.llamaChatHttpTimeoutSeconds.label", "Chat request timeout")}
						description={t(
							"pages.nodeSettings.fields.llamaChatHttpTimeoutSeconds.description",
							"The network timeout for one chat request to llama-server.",
						)}
						bounds={bounds.tunables.llamaChatHttpTimeoutSeconds}
						unit={minutes}
						wireUnit={secondsShort}
						testId="node-settings-llama-chat-http-timeout"
					/>
					<NodeSettingsNumberField
						{...field}
						field="llamaEmbeddingHttpTimeoutSeconds"
						label={t("pages.nodeSettings.fields.llamaEmbeddingHttpTimeoutSeconds.label", "Embedding request timeout")}
						bounds={bounds.tunables.llamaEmbeddingHttpTimeoutSeconds}
						unit={seconds}
						testId="node-settings-llama-embedding-http-timeout"
					/>
				</Stack>
			</Card>
			<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-resource-reserve-card">
				<Stack gap="md">
					<Group justify="space-between" align="center">
						<Title order={2} size="h4">
							{t("pages.nodeSettings.fields.resourceReserve.title", "Resource reserve")}
						</Title>
						<IconGauge size={20} />
					</Group>
					<Text size="sm" c="dimmed">
						{t(
							"pages.nodeSettings.fields.resourceReserve.description",
							"What the model launcher leaves free for the rest of the machine when it sizes a model.",
						)}
					</Text>
					<NodeSettingsNumberField
						{...field}
						field="llamaCpuThreadReserve"
						label={t("pages.nodeSettings.fields.llamaCpuThreadReserve.label", "Reserved CPU threads")}
						description={t(
							"pages.nodeSettings.fields.llamaCpuThreadReserve.description",
							"Only applies when a model runs on the CPU runtime. On a GPU runtime, llama.cpp picks its own thread count.",
						)}
						bounds={bounds.tunables.llamaCpuThreadReserve}
						testId="node-settings-llama-cpu-thread-reserve"
					/>
					<NodeSettingsNumberField
						{...field}
						field="llamaGpuReservePercent"
						label={t("pages.nodeSettings.fields.llamaGpuReservePercent.label", "Reserved GPU memory")}
						bounds={bounds.tunables.llamaGpuReservePercent}
						unit="%"
						testId="node-settings-llama-gpu-reserve"
					/>
					<NodeSettingsNumberField
						{...field}
						field="llamaRamReservePercent"
						label={t("pages.nodeSettings.fields.llamaRamReservePercent.label", "Reserved system memory")}
						bounds={bounds.tunables.llamaRamReservePercent}
						unit="%"
						testId="node-settings-llama-ram-reserve"
					/>
					<NodeSettingsNumberField
						{...field}
						field="modelFitSafetyMarginPercent"
						label={t("pages.nodeSettings.fields.modelFitSafetyMarginPercent.label", "Model fit safety margin")}
						description={t(
							"pages.nodeSettings.fields.modelFitSafetyMarginPercent.description",
							"Headroom the model-fit check adds on top of a model's estimated memory. Applies to the next check.",
						)}
						bounds={bounds.tunables.modelFitSafetyMarginPercent}
						unit="%"
						testId="node-settings-model-fit-safety-margin"
					/>
				</Stack>
			</Card>
			<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-keep-warm-card">
				<Stack gap="md">
					<Group justify="space-between" align="center">
						<Title order={2} size="h4">
							{t("pages.nodeSettings.fields.keepModelWarm.title", "Keep warm")}
						</Title>
						<IconFlame size={20} />
					</Group>
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
								: nodeSettingsFieldError(t, errors, "keepModelWarmModelName")
						}
						data-testid="node-settings-keep-model-warm-model"
					/>
					<NumberInput
						label={t("pages.nodeSettings.fields.keepModelWarm.intervalLabel", "Warm interval")}
						description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.keepModelWarmIntervalSeconds.min}–${bounds.keepModelWarmIntervalSeconds.max} ${t("pages.nodeSettings.fields.seconds", "seconds")}.`}
						suffix={` ${t("pages.nodeSettings.fields.seconds", "seconds")}`}
						min={bounds.keepModelWarmIntervalSeconds.min}
						max={bounds.keepModelWarmIntervalSeconds.max}
						disabled={!form.keepModelWarmEnabled}
						allowDecimal={false}
						value={form.keepModelWarmIntervalSeconds}
						onChange={(value) => onChange("keepModelWarmIntervalSeconds", value)}
						error={nodeSettingsFieldError(t, errors, "keepModelWarmIntervalSeconds")}
						data-testid="node-settings-keep-model-warm-interval"
					/>
					<Text size="xs" c="dimmed" data-testid="node-settings-keep-model-warm-help">
						{t(
							"pages.nodeSettings.fields.keepModelWarm.help",
							"Pinning keeps VRAM occupied and permanently uses one of the configured {{maxLoadedProcesses}} MaxLoadedProcesses slots. The warm interval must remain below the idle TTL to prevent eviction.",
							{ maxLoadedProcesses: form.llamaMaxLoadedProcesses },
						)}
					</Text>
				</Stack>
			</Card>
			<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-launch-tuning-card">
				<Stack gap="md">
					<Group justify="space-between" align="center">
						<Title order={2} size="h4">
							{t("pages.nodeSettings.fields.launchTuning.title", "Chat launch tuning")}
						</Title>
						<IconRocket size={20} />
					</Group>
					<Text size="xs" c="dimmed">
						{t(
							"pages.nodeSettings.fields.runtime.tagHint",
							"The recommended llama.cpp version is managed in the llama.cpp runtime card under Runtimes & builds.",
						)}
					</Text>
					<Select
						label={nodeSettingsFieldLabel(t, "kvCacheType", t("pages.nodeSettings.fields.kvCacheType.label", "KV cache type"))}
						description={t(
							"pages.nodeSettings.fields.kvCacheType.description",
							"Changing this invalidates every frozen inference profile on this node. Each model re-explores with the new KV cache type the next time it loads, and must be benchmarked and frozen again. q4_0 trades answer quality for VRAM.",
						)}
						data={kvCacheTypeOptions}
						value={form.kvCacheType}
						onChange={(value) => onChange("kvCacheType", value ?? KV_CACHE_TYPE_DEFAULT)}
						allowDeselect={false}
						error={nodeSettingsFieldError(t, errors, "kvCacheType")}
						data-testid="node-settings-kv-cache-type"
					/>
					<Select
						label={nodeSettingsFieldLabel(
							t,
							"speculativeMode",
							t("pages.nodeSettings.fields.speculativeMode.label", "Speculative decoding"),
						)}
						description={t(
							"pages.nodeSettings.fields.speculativeMode.description",
							"Draft-and-verify decoding raises single-user throughput. n-gram modes need no extra model; draft models use additional VRAM not yet counted by capacity checks. DFlash and DSpark need their own draft model and are trained for larger draft blocks than the default 3 draft tokens per step (upstream uses 15 and 7); the server only clamps that value downwards, so raise it yourself.",
						)}
						data={speculativeModeOptions}
						value={form.speculativeMode}
						onChange={(value) => onChange("speculativeMode", value ?? SPECULATIVE_DISABLED_MODE)}
						allowDeselect={false}
						error={nodeSettingsFieldError(t, errors, "speculativeMode")}
						data-testid="node-settings-speculative-mode"
					/>
					{needsDraftModel ? (
						<Select
							label={nodeSettingsFieldLabel(
								t,
								"speculativeDraftModelName",
								t("pages.nodeSettings.fields.speculativeDraftModel.label", "Draft model"),
							)}
							description={t(
								"pages.nodeSettings.fields.speculativeDraftModel.description",
								"An installed chat-capable model used as the drafter. Must share the target model's tokenizer family.",
							)}
							placeholder={t("pages.nodeSettings.fields.speculativeDraftModel.placeholder", "Select a draft model")}
							data={[...draftModelOptions]}
							value={form.speculativeDraftModelName === "" ? null : form.speculativeDraftModelName}
							onChange={(value) => onChange("speculativeDraftModelName", value ?? "")}
							searchable={true}
							nothingFoundMessage={t("pages.nodeSettings.fields.speculativeDraftModel.empty", "No installed chat models")}
							error={nodeSettingsFieldError(t, errors, "speculativeDraftModelName")}
							data-testid="node-settings-speculative-draft-model"
						/>
					) : null}
					{needsDraftModel ? (
						<NodeSettingsNumberField
							{...field}
							field="speculativeDraftGpuLayers"
							label={t("pages.nodeSettings.fields.speculativeDraftGpuLayers.label", "Draft model GPU layers")}
							description={t(
								"pages.nodeSettings.fields.speculativeDraftGpuLayers.description",
								"How many draft-model layers are offloaded to the GPU. Leave empty for the runtime default.",
							)}
							bounds={bounds.speculativeDraftGpuLayers}
							testId="node-settings-speculative-draft-gpu-layers"
						/>
					) : null}
					{showsDraftTokensPerStep ? (
						<NumberInput
							label={nodeSettingsFieldLabel(
								t,
								"speculativeDraftMaxTokens",
								t("pages.nodeSettings.fields.speculativeDraftMaxTokens.label", "Draft tokens per step"),
							)}
							description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.speculativeDraftMaxTokens.min}–${bounds.speculativeDraftMaxTokens.max}.`}
							min={bounds.speculativeDraftMaxTokens.min}
							max={bounds.speculativeDraftMaxTokens.max}
							allowDecimal={false}
							value={form.speculativeDraftMaxTokens}
							onChange={(value) => onChange("speculativeDraftMaxTokens", value)}
							error={nodeSettingsFieldError(t, errors, "speculativeDraftMaxTokens")}
							data-testid="node-settings-speculative-draft-max-tokens"
						/>
					) : null}
					<NumberInput
						label={nodeSettingsFieldLabel(
							t,
							"chatCacheReuse",
							t("pages.nodeSettings.fields.chatCacheReuse.label", "Prompt cache reuse"),
						)}
						description={t(
							"pages.nodeSettings.fields.chatCacheReuse.description",
							"Reuse an unchanged prompt prefix across turns (tokens). 0 disables.",
						)}
						min={bounds.chatCacheReuse.min}
						max={bounds.chatCacheReuse.max}
						allowDecimal={false}
						value={form.chatCacheReuse}
						onChange={(value) => onChange("chatCacheReuse", value)}
						error={nodeSettingsFieldError(t, errors, "chatCacheReuse")}
						data-testid="node-settings-chat-cache-reuse"
					/>
					<Stack gap={4}>
						<Text size="sm" fw={500} component="div" id="node-settings-chat-cache-ram-label">
							{nodeSettingsFieldLabel(
								t,
								"llamaChatCacheRamMiB",
								t("pages.nodeSettings.fields.llamaChatCacheRam.label", "Prompt cache memory"),
							)}
						</Text>
						<Text size="xs" c="dimmed">
							{t(
								"pages.nodeSettings.fields.llamaChatCacheRam.description",
								"Host memory llama-server keeps for cached prompts. Automatic uses one eighth of RAM (512–8192 MiB).",
							)}
						</Text>
						<SegmentedControl
							aria-labelledby="node-settings-chat-cache-ram-label"
							value={form.llamaChatCacheRamMode}
							onChange={(value) => onChange("llamaChatCacheRamMode", value as ChatCacheRamMode)}
							data={[
								{ value: "auto", label: t("pages.nodeSettings.fields.llamaChatCacheRam.auto", "Automatic") },
								{ value: "off", label: t("pages.nodeSettings.fields.llamaChatCacheRam.off", "Off") },
								{ value: "custom", label: t("pages.nodeSettings.fields.llamaChatCacheRam.custom", "Custom") },
							]}
							data-testid="node-settings-chat-cache-ram-mode"
						/>
					</Stack>
					{form.llamaChatCacheRamMode === "custom" ? (
						<NodeSettingsNumberField
							{...field}
							field="llamaChatCacheRamMiB"
							label={t("pages.nodeSettings.fields.llamaChatCacheRam.sizeLabel", "Prompt cache size")}
							bounds={{ min: Math.max(1, bounds.llamaChatCacheRamMiB.min), max: bounds.llamaChatCacheRamMiB.max }}
							unit="MiB"
							testId="node-settings-chat-cache-ram-size"
						/>
					) : null}
				</Stack>
			</Card>
		</>
	);
}
