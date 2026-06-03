import { ActionIcon, Box, Button, Group, Menu, Paper, Text, Textarea, Tooltip } from "@mantine/core";
import { IconBrain, IconDeviceDesktop, IconPaperclip, IconPhoto, IconPlayerStopFilled, IconSend, IconUserBolt } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { AgentSelectorCard } from "@/features/chat/components/AgentSelectorCard";
import { ContextUsageBadge } from "@/features/chat/components/ContextUsageBadge";
import { ModelSelectorCard } from "@/features/chat/components/ModelSelectorCard";
import { defaultChatUiCapabilities } from "@/features/chat/models/ChatCapabilityGates";
import type { AgentOption, ChatUiCapabilities, ContextUsageModel, ModelOption, ReasoningEffort } from "@/features/chat/models/ChatModels";

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
	toolsEnabled?: boolean;
	agentControlsAvailable?: boolean;
	agentModeEnabled?: boolean;
	selectedAgentId?: string;
	agentOptions?: readonly AgentOption[];
	onCancel: () => void;
	onModelChange: (model: string) => void;
	onReasoningEffortChange: (effort: ReasoningEffort) => void;
	onToggleTools?: () => void;
	onToggleAgentMode?: () => void;
	onAgentChange?: (agentId: string) => void;
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
	toolsEnabled = false,
	agentControlsAvailable = false,
	agentModeEnabled = false,
	selectedAgentId = "",
	agentOptions = [],
	onCancel,
	onModelChange,
	onReasoningEffortChange,
	onToggleTools,
	onToggleAgentMode,
	onAgentChange,
	onSend,
}: ChatInputAreaProps) {
	const { t } = useTranslation();
	const [content, setContent] = useState("");
	const trimmed = content.trim();
	const reasoningEnabled = reasoningEffort !== "none";
	const reasoningMenuDisabled = disabled || isSending || availableReasoningEfforts.length <= 1;
	const sendDisabled = isSending ? false : disabled || sendDisabledProp || !trimmed;
	// Agent selector is disabled while sending or when there are no agents to pick from.
	const agentSelectorDisabled = disabled || isSending || agentOptions.length === 0;
	// Show a subtle hint when agent mode is on but no agent has been selected yet. Does NOT block send.
	const showNoAgentHint = agentModeEnabled && !selectedAgentId;

	const submit = (): void => {
		if (!trimmed) {
			return;
		}

		const safeEffort = isEffortAvailable(reasoningEffort, availableReasoningEfforts) ? reasoningEffort : (availableReasoningEfforts[0] ?? "none");
		onSend(trimmed, safeEffort, selectedModel);
		setContent("");
	};

	return (
		<Paper data-testid="chat-input-area" withBorder={true} radius="md" p="xs" style={{ background: "var(--mantine-color-body)" }}>
			<Box>
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
					variant="unstyled"
					disabled={disabled || isSending}
					styles={{ input: { paddingLeft: "var(--mantine-spacing-xs)", paddingRight: "var(--mantine-spacing-xs)", background: "transparent" } }}
				/>
				<Group justify="space-between" align="center" wrap="nowrap" gap="xs" mt={4} px={4}>
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
						{capabilities.showLocalToolControls ? (
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
							<Tooltip
								label={
									agentModeEnabled
										? t("pages.chat.agentMode.enabled", "Agent mode enabled")
										: t("pages.chat.agentMode.disabled", "Agent mode disabled")
								}
							>
								<ActionIcon
									size={36}
									variant={agentModeEnabled ? "light" : "subtle"}
									color={agentModeEnabled ? "primary" : "gray"}
									disabled={disabled || isSending || !onToggleAgentMode}
									onClick={onToggleAgentMode}
									aria-label={t("pages.chat.agentMode.toggleLabel", "Agent mode")}
									aria-pressed={agentModeEnabled}
									data-testid="chat-agent-mode-toggle"
								>
									<IconUserBolt size={15} />
								</ActionIcon>
							</Tooltip>
						) : null}
						{agentControlsAvailable && agentModeEnabled ? (
							<AgentSelectorCard
								agentOptions={agentOptions}
								selectedAgentId={selectedAgentId}
								disabled={agentSelectorDisabled}
								onAgentChange={onAgentChange ?? (() => undefined)}
							/>
						) : null}
						{agentControlsAvailable && showNoAgentHint ? (
							<Text size="xs" c="dimmed" style={{ alignSelf: "center" }}>
								{t("pages.chat.agentMode.noAgentHint", "No agent selected")}
							</Text>
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
			</Box>
		</Paper>
	);
}
