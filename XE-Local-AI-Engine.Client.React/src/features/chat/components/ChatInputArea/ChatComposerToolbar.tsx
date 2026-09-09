import { ActionIcon, Button, FileButton, Group, Menu, Tooltip } from "@mantine/core";
import {
	IconAdjustments,
	IconBooks,
	IconBrain,
	IconDeviceDesktop,
	IconPaperclip,
	IconPlayerStopFilled,
	IconSend,
} from "@tabler/icons-react";
import type { RefObject } from "react";
import { useTranslation } from "react-i18next";

import { AgentSelectorCard } from "@/features/chat/components/AgentSelectorCard";
import { CompactButton } from "@/features/chat/components/CompactButton";
import { ContextUsageBadge } from "@/features/chat/components/ContextUsageBadge";
import { ModelSelectorCard } from "@/features/chat/components/ModelSelectorCard";
import type { AgentOption, ContextUsageModel, ModelOption, ReasoningEffort } from "@/features/chat/models/ChatModels";
import { VoiceComposerControls } from "@/features/voice/components/VoiceComposerControls";

interface ChatComposerToolbarProps {
	toolbarRef: RefObject<HTMLDivElement | null>;
	disabled: boolean;
	isSending: boolean;
	modelOptions: ModelOption[];
	cloudModelOptions?: ModelOption[];
	selectedModel: string;
	modelSelectorDisabled: boolean;
	onModelChange: (model: string) => void;
	availableReasoningEfforts: ReasoningEffort[];
	reasoningEffort: ReasoningEffort;
	reasoningEnabled: boolean;
	reasoningMenuDisabled: boolean;
	onReasoningEffortChange: (effort: ReasoningEffort) => void;
	showLocalToolControls: boolean;
	toolsEnabled: boolean;
	onToggleTools?: () => void;
	showKnowledgeBaseControls: boolean;
	knowledgeBaseEnabled: boolean;
	knowledgeBaseHasDocuments: boolean;
	onToggleKnowledgeBase?: () => void;
	agentControlsAvailable: boolean;
	agentOptions: readonly AgentOption[];
	agentModeEnabled: boolean;
	selectedAgentId: string;
	agentSelectorDisabled: boolean;
	onSelectAgent?: (agentId: string) => void;
	attachmentControlsAvailable: boolean;
	attachmentControlsDisabled: boolean;
	attachmentAccept: string;
	onPickFiles: (files: File[] | null) => void;
	showSamplingOptions: boolean;
	onOpenSamplingOptions: () => void;
	showVoiceControls: boolean;
	showContextUsage: boolean;
	contextUsage?: ContextUsageModel;
	sendDisabled: boolean;
	onCancel: () => void;
	onSubmit: () => void;
}

// The toolbar is hosted in the Textarea's bottomSection (rendered inside the input border, pointer-events:all —
// so the Stop button stays interactive even while the input element itself is disabled during a send). A
// single wrap="wrap" Group (rather than nowrap) lets the controls reflow onto extra rows instead of
// overlapping on narrow panes; the Send button carries its own auto left-margin so it always lands at the
// right edge of whichever row it ends up on, including a row of its own.
export function ChatComposerToolbar({
	toolbarRef,
	disabled,
	isSending,
	modelOptions,
	cloudModelOptions,
	selectedModel,
	modelSelectorDisabled,
	onModelChange,
	availableReasoningEfforts,
	reasoningEffort,
	reasoningEnabled,
	reasoningMenuDisabled,
	onReasoningEffortChange,
	showLocalToolControls,
	toolsEnabled,
	onToggleTools,
	showKnowledgeBaseControls,
	knowledgeBaseEnabled,
	knowledgeBaseHasDocuments,
	onToggleKnowledgeBase,
	agentControlsAvailable,
	agentOptions,
	agentModeEnabled,
	selectedAgentId,
	agentSelectorDisabled,
	onSelectAgent,
	attachmentControlsAvailable,
	attachmentControlsDisabled,
	attachmentAccept,
	onPickFiles,
	showSamplingOptions,
	onOpenSamplingOptions,
	showVoiceControls,
	showContextUsage,
	contextUsage,
	sendDisabled,
	onCancel,
	onSubmit,
}: ChatComposerToolbarProps) {
	const { t } = useTranslation();

	return (
		<Group ref={toolbarRef} align="center" wrap="wrap" gap="xs" style={{ width: "100%" }}>
			<Group gap={4} wrap="wrap" style={{ flex: 1, minWidth: 0 }}>
				<ModelSelectorCard
					modelOptions={modelOptions}
					cloudModelOptions={cloudModelOptions}
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
							<Menu.Item
								key={effort}
								data-testid={`chat-reasoning-effort-option-${effort}`}
								onClick={() => onReasoningEffortChange(effort)}
								color={effort === reasoningEffort && effort !== "none" ? "primary" : undefined}
							>
								{t(`pages.chat.reasoningEffortOptions.${effort}`, effort)}
							</Menu.Item>
						))}
					</Menu.Dropdown>
				</Menu>
				{showLocalToolControls ? (
					<Tooltip
						label={
							toolsEnabled
								? t("pages.chat.localToolsEnabled", "Local tools enabled")
								: t("pages.chat.localToolsDisabled", "Local tools disabled")
						}
					>
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
				{showKnowledgeBaseControls ? (
					<Tooltip
						label={
							!knowledgeBaseHasDocuments
								? t("pages.chat.knowledgeBaseNoDocuments", "No indexed documents to search")
								: knowledgeBaseEnabled
									? t("pages.chat.knowledgeBaseEnabled", "Knowledge base enabled")
									: t("pages.chat.knowledgeBaseDisabled", "Knowledge base disabled")
						}
					>
						<ActionIcon
							size={36}
							variant={knowledgeBaseEnabled && knowledgeBaseHasDocuments ? "light" : "subtle"}
							color={knowledgeBaseEnabled && knowledgeBaseHasDocuments ? "primary" : "gray"}
							// Disabled with no indexed docs: grounding on an empty corpus is a no-op. The persisted
							// enabled-preference is untouched (the store keeps it), so it re-arms once a doc is indexed.
							disabled={disabled || isSending || !onToggleKnowledgeBase || !knowledgeBaseHasDocuments}
							onClick={onToggleKnowledgeBase}
							aria-label={t("pages.chat.knowledgeBaseLabel", "Use knowledge base")}
							aria-pressed={knowledgeBaseEnabled && knowledgeBaseHasDocuments}
							data-testid="chat-knowledge-base-toggle"
						>
							<IconBooks size={15} />
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
				{attachmentControlsAvailable ? (
					<FileButton onChange={onPickFiles} multiple={true} accept={attachmentAccept}>
						{(fileButtonProps) => (
							<Tooltip label={t("pages.chat.composer.attach", "Attach file")}>
								<ActionIcon
									{...fileButtonProps}
									size={36}
									variant="subtle"
									color="gray"
									disabled={attachmentControlsDisabled}
									aria-label={t("pages.chat.composer.attach", "Attach file")}
									data-testid="chat-attach-file-trigger"
								>
									<IconPaperclip size={15} />
								</ActionIcon>
							</Tooltip>
						)}
					</FileButton>
				) : null}
				{showSamplingOptions ? (
					<Tooltip label={t("pages.chat.composer.samplingOptions", "Advanced sampling options")}>
						<ActionIcon
							size={36}
							variant="subtle"
							color="gray"
							onClick={onOpenSamplingOptions}
							aria-label={t("pages.chat.composer.samplingOptions", "Advanced sampling options")}
							data-testid="chat-sampling-options-trigger"
						>
							<IconAdjustments size={15} />
						</ActionIcon>
					</Tooltip>
				) : null}
				{showVoiceControls ? <VoiceComposerControls /> : null}
				{showContextUsage && contextUsage ? (
					<Group gap={4} wrap="nowrap">
						<ContextUsageBadge {...contextUsage} />
						<CompactButton
							percentUsed={
								contextUsage.usedTokens !== undefined && contextUsage.maxTokens !== undefined && contextUsage.maxTokens > 0
									? (contextUsage.usedTokens / contextUsage.maxTokens) * 100
									: undefined
							}
							// Read-only (e.g. remote) conversations reject the mutation with 409, and a live turn is already
							// driving the local runtime — disable compaction in both cases, mirroring the composer.
							disabled={disabled || isSending}
						/>
					</Group>
				) : null}
			</Group>
			<Button
				data-testid="chat-send-button"
				onClick={() => {
					if (isSending) {
						onCancel();
						return;
					}
					onSubmit();
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
}
