import { ActionIcon, Box, Button, Group, Menu, Textarea, Tooltip } from "@mantine/core";
import { IconAdjustments, IconBrain, IconDeviceDesktop, IconPaperclip, IconPhoto, IconPlayerStopFilled, IconSend } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { AgentSelectorCard } from "@/features/chat/components/AgentSelectorCard";
import { ChatSamplingOptionsDialog } from "@/features/chat/components/ChatSamplingOptionsDialog";
import { ContextUsageBadge } from "@/features/chat/components/ContextUsageBadge";
import { ModelSelectorCard } from "@/features/chat/components/ModelSelectorCard";
import { defaultChatUiCapabilities } from "@/features/chat/models/ChatCapabilityGates";
import type { AgentOption, ChatUiCapabilities, ContextUsageModel, ModelOption, ReasoningEffort } from "@/features/chat/models/ChatModels";

// The composer toolbar lives inside the Textarea's native bottomSection (Mantine 9.3+). The section has a fixed
// height driven by --input-bottom-section-height (28px default); we override it to fit the 36px control row and
// match the input's bottom padding so the typed text never sits under the toolbar.
const TOOLBAR_HEIGHT = "3rem";
const composerStyles = {
	input: { paddingBottom: `calc(${TOOLBAR_HEIGHT} + var(--mantine-spacing-xs))` },
	bottomSection: { height: TOOLBAR_HEIGHT },
} as const;

interface ChatInputAreaProps {
	availableReasoningEfforts: ReasoningEffort[];
	capabilities?: ChatUiCapabilities;
	contextUsage?: ContextUsageModel;
	disabled?: boolean;
	isSending: boolean;
	modelOptions: ModelOption[];
	modelSelectorDisabled?: boolean;
	sendDisabled?: boolean;
	selectedModel: string;
	reasoningEffort: ReasoningEffort;
	// Whether the active model advertises the Ollama `tools` capability. Combined with the node-wide
	// capability to gate the local-tool controls so a non-tool model never offers them.
	activeModelToolCapable?: boolean;
	toolsEnabled?: boolean;
	agentControlsAvailable?: boolean;
	agentModeEnabled?: boolean;
	selectedAgentId?: string;
	agentOptions?: readonly AgentOption[];
	onCancel: () => void;
	onModelChange: (model: string) => void;
	onReasoningEffortChange: (effort: ReasoningEffort) => void;
	onToggleTools?: () => void;
	// Single merged agent control: "" => Default Assistant (agent mode off); any other id => enable mode + stamp it.
	onSelectAgent?: (agentId: string) => void;
	onSend: (content: string, effort: ReasoningEffort, model: string) => void;
}

function isEffortAvailable(effort: ReasoningEffort, availableEfforts: ReasoningEffort[]): boolean {
	return availableEfforts.includes(effort);
}

export function ChatInputArea({
	availableReasoningEfforts,
	capabilities = defaultChatUiCapabilities,
	contextUsage,
	disabled = false,
	isSending,
	modelOptions,
	modelSelectorDisabled = false,
	sendDisabled: sendDisabledProp = false,
	selectedModel,
	reasoningEffort,
	activeModelToolCapable = false,
	toolsEnabled = false,
	agentControlsAvailable = false,
	agentModeEnabled = false,
	selectedAgentId = "",
	agentOptions = [],
	onCancel,
	onModelChange,
	onReasoningEffortChange,
	onToggleTools,
	onSelectAgent,
	onSend,
}: ChatInputAreaProps) {
	const { t } = useTranslation();
	const [content, setContent] = useState("");
	const [samplingDialogOpen, setSamplingDialogOpen] = useState(false);
	// Read developer mode directly from the global store — avoids prop-drilling through ChatDisplayShell.
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const trimmed = content.trim();
	const reasoningEnabled = reasoningEffort !== "none";
	const reasoningMenuDisabled = disabled || isSending || availableReasoningEfforts.length <= 1;
	// Local-tool controls require BOTH the node-wide capability AND the active model advertising the Ollama
	// `tools` capability — a model that can't call tools must never be offered them.
	const showLocalToolControls = capabilities.showLocalToolControls && activeModelToolCapable;
	const sendDisabled = isSending ? false : disabled || sendDisabledProp || !trimmed;
	// Agent selector is disabled while sending or when there are no agents to pick from.
	const agentSelectorDisabled = disabled || isSending || agentOptions.length === 0;

	const submit = (): void => {
		if (!trimmed) {
			return;
		}

		const safeEffort = isEffortAvailable(reasoningEffort, availableReasoningEfforts) ? reasoningEffort : (availableReasoningEfforts[0] ?? "none");
		onSend(trimmed, safeEffort, selectedModel);
		setContent("");
	};

	// The toolbar is hosted in the Textarea's bottomSection (rendered inside the input border, pointer-events:all —
	// so the Stop button stays interactive even while the input element itself is disabled during a send).
	const toolbar = (
		<Group justify="space-between" align="center" wrap="nowrap" gap="xs" style={{ width: "100%" }}>
			<Group gap={4} wrap="nowrap" style={{ flex: 1, minWidth: 0 }}>
				<ModelSelectorCard
					modelOptions={modelOptions}
					selectedModel={selectedModel}
					disabled={modelSelectorDisabled || isSending || modelOptions.length === 0}
					onModelChange={onModelChange}
				/>
				<Menu position="top-start" offset={8} withinPortal={true} disabled={reasoningMenuDisabled}>
					<Menu.Target>
						<Tooltip label={t("pages.chat.reasoningEffortLabel", "Reasoning effort")}>
							<ActionIcon
								size={36}
								variant={reasoningEnabled ? "light" : "subtle"}
								color={reasoningEnabled ? "primary" : "gray"}
								disabled={reasoningMenuDisabled}
								aria-label={t("pages.chat.reasoningEffortLabel", "Reasoning effort")}
								data-testid="chat-reasoning-effort-menu-trigger"
							>
								<IconBrain size={15} />
							</ActionIcon>
						</Tooltip>
					</Menu.Target>
					<Menu.Dropdown>
						<Menu.Label>{t("pages.chat.reasoningEffortLabel", "Reasoning effort")}</Menu.Label>
						{availableReasoningEfforts.map((effort) => (
							<Menu.Item key={effort} data-testid={`chat-reasoning-effort-option-${effort}`} onClick={() => onReasoningEffortChange(effort)} color={effort === reasoningEffort && effort !== "none" ? "primary" : undefined}>
								{t(`pages.chat.reasoningEffortOptions.${effort}`, effort)}
							</Menu.Item>
						))}
					</Menu.Dropdown>
				</Menu>
				{showLocalToolControls ? (
					<Tooltip label={toolsEnabled ? t("pages.chat.localToolsEnabled", "Local tools enabled") : t("pages.chat.localToolsDisabled", "Local tools disabled")}>
						<ActionIcon
							size={36}
							variant={toolsEnabled ? "light" : "subtle"}
							color={toolsEnabled ? "primary" : "gray"}
							disabled={disabled || isSending || !onToggleTools}
							onClick={onToggleTools}
							aria-label={t("pages.chat.localToolsLabel", "Local tools")}
							aria-pressed={toolsEnabled}
							data-testid="chat-local-tools-toggle"
						>
							<IconDeviceDesktop size={15} />
						</ActionIcon>
					</Tooltip>
				) : null}
				{agentControlsAvailable ? (
					<AgentSelectorCard
						agentOptions={agentOptions}
						agentModeEnabled={agentModeEnabled}
						selectedAgentId={selectedAgentId}
						disabled={agentSelectorDisabled}
						onSelectAgent={onSelectAgent ?? (() => undefined)}
					/>
				) : null}
				{capabilities.showFileAttachmentControls ? (
					<Tooltip label={t("pages.chat.composer.attach", "Attach file")}>
						<ActionIcon size={36} variant="subtle" color="gray" disabled={true} aria-label="Attach file">
							<IconPaperclip size={15} />
						</ActionIcon>
					</Tooltip>
				) : null}
				{capabilities.showImageAttachmentControls ? (
					<Tooltip label={t("pages.chat.composer.image", "Attach image")}>
						<ActionIcon size={36} variant="subtle" color="gray" disabled={true} aria-label="Attach image">
							<IconPhoto size={15} />
						</ActionIcon>
					</Tooltip>
				) : null}
				{developerMode ? (
					<Tooltip label={t("pages.chat.composer.samplingOptions", "Advanced sampling options")}>
						<ActionIcon
							size={36}
							variant="subtle"
							color="gray"
							onClick={() => setSamplingDialogOpen(true)}
							aria-label={t("pages.chat.composer.samplingOptions", "Advanced sampling options")}
							data-testid="chat-sampling-options-trigger"
						>
							<IconAdjustments size={15} />
						</ActionIcon>
					</Tooltip>
				) : null}
				{contextUsage ? <ContextUsageBadge {...contextUsage} /> : null}
			</Group>
			<Button
				data-testid="chat-send-button"
				onClick={() => {
					if (isSending) {
						onCancel();
						return;
					}
					submit();
				}}
				disabled={sendDisabled}
				color={isSending ? "red" : "dark"}
				size="sm"
				style={{ flexShrink: 0 }}
				leftSection={isSending ? <IconPlayerStopFilled size={13} /> : <IconSend size={13} />}
				aria-label={isSending ? t("pages.chat.stop", "Stop") : t("pages.chat.send", "Send")}
			>
				{isSending ? t("pages.chat.stop", "Stop") : t("pages.chat.send", "Send")}
			</Button>
		</Group>
	);

	return (
		<Box data-testid="chat-input-area">
			{developerMode ? (
				<ChatSamplingOptionsDialog
					opened={samplingDialogOpen}
					onClose={() => setSamplingDialogOpen(false)}
					maxContextTokens={contextUsage?.maxTokens}
				/>
			) : null}
			<Textarea
				data-testid="chat-input"
				placeholder={t("pages.chat.inputPlaceholder", "Message the local node")}
				value={content}
				onChange={(event) => setContent(event.currentTarget.value)}
				onKeyDown={(event) => {
					if (event.key === "Enter" && !event.shiftKey) {
						event.preventDefault();
						submit();
					}
				}}
				autosize={true}
				minRows={2}
				maxRows={8}
				radius="md"
				disabled={disabled || isSending}
				bottomSection={toolbar}
				styles={composerStyles}
			/>
		</Box>
	);
}
