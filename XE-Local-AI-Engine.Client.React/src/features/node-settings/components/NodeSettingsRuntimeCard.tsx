import { Card, Group, NumberInput, Select, Stack, Switch, Text, TextInput, Title } from "@mantine/core";
import { IconCpu } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import {
	nodeSettingsFieldError,
	nodeSettingsRestartHint,
} from "@/features/node-settings/components/NodeSettingsFieldPresentation";
import {
	type NodeSettingsFieldBounds,
	type NodeSettingsFieldsForm,
	requiresExternalDraftModel,
	SPECULATIVE_DISABLED_MODE,
	speculativeModeSelectValues,
	usesDraftTokensPerStep,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";

export interface NodeSettingsModelOption {
	readonly value: string;
	readonly label: string;
}
interface Props {
	readonly form: NodeSettingsFieldsForm;
	readonly bounds: NodeSettingsFieldBounds;
	readonly errors: Readonly<Record<string, string>>;
	readonly onChange: <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]) => void;
	readonly draftModelOptions: readonly NodeSettingsModelOption[];
	readonly keepWarmModelOptions: readonly NodeSettingsModelOption[];
	readonly autoEffortFastModelOptions: readonly NodeSettingsModelOption[];
}
export function NodeSettingsRuntimeCard({
	form,
	bounds,
	errors,
	onChange,
	draftModelOptions,
	keepWarmModelOptions,
	autoEffortFastModelOptions,
}: Props) {
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
	const needsDraftModel = requiresExternalDraftModel(form.speculativeMode);
	const showsDraftTokensPerStep = usesDraftTokensPerStep(form.speculativeMode);
	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="node-settings-runtime-card">
			<Stack gap="md">
				<Group justify="space-between" align="center">
					<Title order={4}>{t("pages.nodeSettings.fields.runtime.title", "Local model runtime")}</Title>
					<IconCpu size={20} />
				</Group>
				<NumberInput
					label={t("pages.nodeSettings.fields.llamaMaxLoadedProcesses.label", "Max loaded llama-server processes")}
					description={
						<>
							{`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.llamaMaxLoadedProcesses.min}–${bounds.llamaMaxLoadedProcesses.max}.`}
							{nodeSettingsRestartHint(t, "llamaMaxLoadedProcesses")}
						</>
					}
					min={bounds.llamaMaxLoadedProcesses.min}
					max={bounds.llamaMaxLoadedProcesses.max}
					allowDecimal={false}
					value={form.llamaMaxLoadedProcesses}
					onChange={(value) => onChange("llamaMaxLoadedProcesses", value)}
					error={nodeSettingsFieldError(t, errors, "llamaMaxLoadedProcesses")}
					data-testid="node-settings-llama-max-processes"
				/>
				<NumberInput
					label={t("pages.nodeSettings.fields.llamaIdleTimeToLiveSeconds.label", "Idle process time-to-live")}
					description={
						<>
							{`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.llamaIdleTimeToLiveSeconds.min}–${bounds.llamaIdleTimeToLiveSeconds.max} ${t("pages.nodeSettings.fields.seconds", "seconds")}.`}
							{nodeSettingsRestartHint(t, "llamaIdleTimeToLiveSeconds")}
						</>
					}
					suffix={` ${t("pages.nodeSettings.fields.seconds", "seconds")}`}
					min={bounds.llamaIdleTimeToLiveSeconds.min}
					max={bounds.llamaIdleTimeToLiveSeconds.max}
					allowDecimal={false}
					value={form.llamaIdleTimeToLiveSeconds}
					onChange={(value) => onChange("llamaIdleTimeToLiveSeconds", value)}
					error={nodeSettingsFieldError(t, errors, "llamaIdleTimeToLiveSeconds")}
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
					description={t("pages.nodeSettings.fields.keepModelWarm.modelDescription", "Choose an installed llama.cpp chat model.")}
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
				<TextInput
					label={t("pages.nodeSettings.fields.ollamaEndpoint.label", "Ollama endpoint")}
					description={
						<>
							{t("pages.nodeSettings.fields.ollamaEndpoint.description", "The Ollama API base URL.")}
							{nodeSettingsRestartHint(t, "ollamaEndpoint")}
						</>
					}
					placeholder="http://127.0.0.1:11434"
					value={form.ollamaEndpoint}
					onChange={(event) => onChange("ollamaEndpoint", event.currentTarget.value)}
					error={nodeSettingsFieldError(t, errors, "ollamaEndpoint")}
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
					description={
						<>
							{t(
								"pages.nodeSettings.fields.speculativeMode.description",
								"Draft-and-verify decoding raises single-user throughput. n-gram modes need no extra model; draft models use additional VRAM not yet counted by capacity checks.",
							)}
							{nodeSettingsRestartHint(t, "speculativeMode")}
						</>
					}
					data={speculativeModeOptions}
					value={form.speculativeMode}
					onChange={(value) => onChange("speculativeMode", value ?? SPECULATIVE_DISABLED_MODE)}
					allowDeselect={false}
					error={nodeSettingsFieldError(t, errors, "speculativeMode")}
					data-testid="node-settings-speculative-mode"
				/>
				{needsDraftModel ? (
					<Select
						label={t("pages.nodeSettings.fields.speculativeDraftModel.label", "Draft model")}
						description={
							<>
								{t(
									"pages.nodeSettings.fields.speculativeDraftModel.description",
									"An installed chat-capable model used as the drafter. Must share the target model's tokenizer family.",
								)}
								{nodeSettingsRestartHint(t, "speculativeDraftModelName")}
							</>
						}
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
				{showsDraftTokensPerStep ? (
					<NumberInput
						label={t("pages.nodeSettings.fields.speculativeDraftMaxTokens.label", "Draft tokens per step")}
						description={
							<>
								{`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.speculativeDraftMaxTokens.min}–${bounds.speculativeDraftMaxTokens.max}.`}
								{nodeSettingsRestartHint(t, "speculativeDraftMaxTokens")}
							</>
						}
						min={bounds.speculativeDraftMaxTokens.min}
						max={bounds.speculativeDraftMaxTokens.max}
						allowDecimal={false}
						value={form.speculativeDraftMaxTokens}
						onChange={(value) => onChange("speculativeDraftMaxTokens", value)}
						error={nodeSettingsFieldError(t, errors, "speculativeDraftMaxTokens")}
						data-testid="node-settings-speculative-draft-max-tokens"
					/>
				) : null}
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
				<NumberInput
					label={t("pages.nodeSettings.fields.chatCacheReuse.label", "Prompt cache reuse")}
					description={
						<>
							{t(
								"pages.nodeSettings.fields.chatCacheReuse.description",
								"Reuse an unchanged prompt prefix across turns (tokens). 0 disables.",
							)}
							{nodeSettingsRestartHint(t, "chatCacheReuse")}
						</>
					}
					min={bounds.chatCacheReuse.min}
					max={bounds.chatCacheReuse.max}
					allowDecimal={false}
					value={form.chatCacheReuse}
					onChange={(value) => onChange("chatCacheReuse", value)}
					error={nodeSettingsFieldError(t, errors, "chatCacheReuse")}
					data-testid="node-settings-chat-cache-reuse"
				/>
			</Stack>
		</Card>
	);
}
