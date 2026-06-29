import { Card, Group, NumberInput, Stack, Switch, TagsInput, Text, TextInput, Title } from "@mantine/core";
import { IconCpu, IconRobot, IconServer, IconTool } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { NodeSettingsFieldBounds, NodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";

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

export function NodeSettingsFieldsCard({ form, bounds, errors, onChange, showDeveloperFields }: NodeSettingsFieldsCardProps) {
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
							"Allow local chat agents to call tools.",
						)}
						checked={form.enableTools}
						onChange={(event) => onChange("enableTools", event.currentTarget.checked)}
						data-testid="node-settings-enable-tools"
					/>
					<TagsInput
						label={t("pages.nodeSettings.fields.toolCapableModels.label", "Tool-capable models")}
						description={t(
							"pages.nodeSettings.fields.toolCapableModels.description",
							"Model names that support tool calling. Press Enter to add each name.",
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
						description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.llamaMaxLoadedProcesses.min}–${bounds.llamaMaxLoadedProcesses.max}.`}
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
						description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.llamaIdleTimeToLiveSeconds.min}–${bounds.llamaIdleTimeToLiveSeconds.max} ${t("pages.nodeSettings.fields.seconds", "seconds")}.`}
						suffix={` ${t("pages.nodeSettings.fields.seconds", "seconds")}`}
						min={bounds.llamaIdleTimeToLiveSeconds.min}
						max={bounds.llamaIdleTimeToLiveSeconds.max}
						allowDecimal={false}
						value={form.llamaIdleTimeToLiveSeconds}
						onChange={(value) => onChange("llamaIdleTimeToLiveSeconds", value)}
						error={fieldError(t, errors, "llamaIdleTimeToLiveSeconds")}
						data-testid="node-settings-llama-idle-ttl"
					/>
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
							"Preferred GGUF quantization when downloading from Hugging Face (e.g. Q4_K_M).",
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
						description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.maxResponseSizeMb.min}–${bounds.maxResponseSizeMb.max} MB.`}
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
								"Low-level orchestration and AgentHome limits. Most changes apply after a restart.",
							)}
						</Text>
						<NumberInput
							label={t("pages.nodeSettings.fields.orchestrationIdleTimeoutSeconds.label", "Orchestration idle timeout")}
							description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.orchestrationIdleTimeoutSeconds.min}–${bounds.orchestrationIdleTimeoutSeconds.max} ${t("pages.nodeSettings.fields.seconds", "seconds")}.`}
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
							description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.agentHomeTimeoutSeconds.min}–${bounds.agentHomeTimeoutSeconds.max} ${t("pages.nodeSettings.fields.seconds", "seconds")}.`}
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
							description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.agentHomeTimeoutSeconds.min}–${bounds.agentHomeTimeoutSeconds.max} ${t("pages.nodeSettings.fields.seconds", "seconds")}.`}
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
							description={t("pages.nodeSettings.fields.bytesPositive", "A positive number of bytes.")}
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
							description={t("pages.nodeSettings.fields.bytesPositive", "A positive number of bytes.")}
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
							description={`${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${bounds.maxPendingToolCallAgeMinutes.min}–${bounds.maxPendingToolCallAgeMinutes.max} ${t("pages.nodeSettings.fields.minutes", "minutes")}.`}
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
