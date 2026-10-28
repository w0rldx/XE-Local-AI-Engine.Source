import { ActionIcon, Button, FileButton, Group, Menu, Tooltip } from "@mantine/core";
import {
	IconAdjustments,
	IconBooks,
	IconBrain,
	IconCheck,
	IconDeviceDesktop,
	IconDots,
	IconPaperclip,
	IconPlayerStopFilled,
	IconSend,
} from "@tabler/icons-react";
import type { ReactNode, RefObject } from "react";
import { useTranslation } from "react-i18next";

import { DisabledReasonTooltip } from "@/core/ui/components/DisabledReasonTooltip/DisabledReasonTooltip";
import { AgentSelectorCard } from "@/features/chat/components/AgentSelectorCard";
import { AnswerNowButton } from "@/features/chat/components/ChatInputArea/AnswerNowButton";
import { WebContentAutoAcceptToggle } from "@/features/chat/components/ChatInputArea/WebContentAutoAcceptToggle";
import { CompactButton } from "@/features/chat/components/CompactButton";
import { ContextStateButton } from "@/features/chat/components/ContextStatePanel";
import { ContextUsagePopover } from "@/features/chat/components/ContextUsagePopover/ContextUsagePopover";
import { ModelSelectorCard } from "@/features/chat/components/ModelSelectorCard";
import type { AgentOption, ContextUsageModel, ModelOption, ReasoningEffort } from "@/features/chat/models/ChatModels";
import { toNodeChatRequestModel } from "@/features/chat/models/NodeChatModelSelection";
import { VoiceComposerControls } from "@/features/voice/components/VoiceComposerControls";

import classes from "./ChatComposerToolbar.module.css";

interface ChatComposerToolbarProps {
	toolbarRef: RefObject<HTMLDivElement | null>;
	/** Phone viewport or a composer too narrow even without the context readout and Send's label: the secondary
	 * toggles fold into an overflow menu. */
	compact: boolean;
	/** Send shows its label; off on a phone, and on a composer that needs the room. */
	labelledSend: boolean;
	/** The agent and workflow pickers show their current choice; off on a phone, and on a composer that needs the room. */
	labelledPickers: boolean;
	/** Reports whether the web review switch is on screen (see WebContentAutoAcceptToggle). */
	onWebReviewShownChange: (shown: boolean) => void;
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
	conversationId: string;
	toolsEnabled: boolean;
	onToggleTools?: () => void;
	showKnowledgeBaseControls: boolean;
	knowledgeBaseEnabled: boolean;
	knowledgeBaseHasDocuments: boolean;
	onToggleKnowledgeBase?: () => void;
	agentControlsAvailable: boolean;
	agentOptions: readonly AgentOption[];
	agentModeEnabled: boolean;
	conversationAgentBound: boolean;
	selectedAgentId: string;
	agentSelectorDisabled: boolean;
	onSelectAgent?: (agentId: string) => void;
	/** Chat workflow mode's picker, rendered beside the agent picker. */
	workflowSelector?: ReactNode;
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
	// The streaming message "Answer now" targets; undefined hides the button.
	answerNowMessageId?: string;
	onCancel: () => void;
	onSubmit: () => void;
}

// The toolbar is hosted in the Textarea's bottomSection (rendered inside the input border, pointer-events:all —
// so the Stop button stays interactive even while the input element itself is disabled during a send). It is ONE
// row at every width: instead of wrapping, the model trigger shrinks behind an ellipsis and, when fitComposerToolbar
// says the row needs the room, the agent and workflow pickers fold to icon-only squares (ChatComposerToolbar.module.css),
// so the context readout and Send keep their place on the same line.
export function ChatComposerToolbar({
	toolbarRef,
	compact,
	labelledSend,
	labelledPickers,
	onWebReviewShownChange,
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
	conversationId,
	toolsEnabled,
	onToggleTools,
	showKnowledgeBaseControls,
	knowledgeBaseEnabled,
	knowledgeBaseHasDocuments,
	onToggleKnowledgeBase,
	agentControlsAvailable,
	agentOptions,
	agentModeEnabled,
	conversationAgentBound,
	selectedAgentId,
	agentSelectorDisabled,
	onSelectAgent,
	workflowSelector,
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
	answerNowMessageId,
	onCancel,
	onSubmit,
}: ChatComposerToolbarProps) {
	const { t } = useTranslation();
	const reasoningLabel = t("pages.chat.reasoningEffortLabel", "Reasoning effort");
	const reasoningItems = (
		<>
			<Menu.Label>{reasoningLabel}</Menu.Label>
			{availableReasoningEfforts.map((effort) => (
				<Menu.Item
					key={effort}
					data-testid={`chat-reasoning-effort-option-${effort}`}
					disabled={reasoningMenuDisabled}
					onClick={() => onReasoningEffortChange(effort)}
					color={effort === reasoningEffort && effort !== "none" ? "primary" : undefined}
				>
					{t(`pages.chat.reasoningEffortOptions.${effort}`, effort)}
				</Menu.Item>
			))}
		</>
	);
	const sendControls = (
		<>
			{isSending && answerNowMessageId ? <AnswerNowButton messageId={answerNowMessageId} /> : null}
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
				px={labelledSend ? undefined : "xs"}
				style={{ flexShrink: 0 }}
				leftSection={labelledSend ? isSending ? <IconPlayerStopFilled size={13} /> : <IconSend size={13} /> : undefined}
				aria-label={isSending ? t("pages.chat.stop", "Stop") : t("pages.chat.send", "Send")}
			>
				{!labelledSend ? (
					isSending ? (
						<IconPlayerStopFilled size={15} />
					) : (
						<IconSend size={15} />
					)
				) : isSending ? (
					t("pages.chat.stop", "Stop")
				) : (
					t("pages.chat.send", "Send")
				)}
			</Button>
		</>
	);

	// Compact: the reasoning, local-tools, knowledge-base and sampling toggles fold into one overflow menu, and Send joins
	// the row instead of reserving a column of its own, so a phone keeps the composer to one row.
	// The folded toggles are out of sight, so the trigger carries the same active look the desktop toggles have whenever
	// any of them is on — otherwise a phone sends with reasoning, local tools or the knowledge base enabled unannounced.
	const overflowActive =
		reasoningEnabled ||
		(showLocalToolControls && toolsEnabled) ||
		(showKnowledgeBaseControls && knowledgeBaseEnabled && knowledgeBaseHasDocuments);
	const overflowMenu = compact ? (
		<Menu position="top-end" offset={8} withinPortal={true}>
			<Menu.Target>
				<Tooltip label={t("pages.chat.composer.moreControls", "More composer options")}>
					<ActionIcon
						size={36}
						variant={overflowActive ? "light" : "subtle"}
						color={overflowActive ? "primary" : "gray"}
						data-active={overflowActive ? "true" : undefined}
						aria-label={t("pages.chat.composer.moreControls", "More composer options")}
						data-testid="chat-composer-overflow-trigger"
					>
						<IconDots size={15} />
					</ActionIcon>
				</Tooltip>
			</Menu.Target>
			<Menu.Dropdown>
				{reasoningItems}
				{showLocalToolControls || showKnowledgeBaseControls || showSamplingOptions ? <Menu.Divider /> : null}
				{showLocalToolControls ? (
					<Menu.Item
						leftSection={<IconDeviceDesktop size={15} />}
						rightSection={toolsEnabled ? <IconCheck size={15} /> : null}
						disabled={disabled || isSending || !onToggleTools}
						onClick={onToggleTools}
						data-testid="chat-local-tools-toggle"
					>
						{toolsEnabled
							? t("pages.chat.localToolsEnabled", "Local tools enabled")
							: t("pages.chat.localToolsDisabled", "Local tools disabled")}
					</Menu.Item>
				) : null}
				{showKnowledgeBaseControls ? (
					<Menu.Item
						leftSection={<IconBooks size={15} />}
						rightSection={knowledgeBaseEnabled && knowledgeBaseHasDocuments ? <IconCheck size={15} /> : null}
						disabled={disabled || isSending || !onToggleKnowledgeBase || !knowledgeBaseHasDocuments}
						onClick={onToggleKnowledgeBase}
						data-testid="chat-knowledge-base-toggle"
					>
						{!knowledgeBaseHasDocuments
							? t("pages.chat.knowledgeBaseNoDocuments", "No indexed documents to search")
							: knowledgeBaseEnabled
								? t("pages.chat.knowledgeBaseEnabled", "Knowledge base enabled")
								: t("pages.chat.knowledgeBaseDisabled", "Knowledge base disabled")}
					</Menu.Item>
				) : null}
				{showSamplingOptions ? (
					<Menu.Item
						leftSection={<IconAdjustments size={15} />}
						onClick={onOpenSamplingOptions}
						data-testid="chat-sampling-options-trigger"
					>
						{t("pages.chat.composer.samplingOptions", "Advanced sampling options")}
					</Menu.Item>
				) : null}
			</Menu.Dropdown>
		</Menu>
	) : null;

	return (
		<Group
			ref={toolbarRef}
			align="center"
			wrap="nowrap"
			gap="xs"
			preventGrowOverflow={false}
			className={classes["toolbar"]}
			data-folded-pickers={labelledPickers ? undefined : "true"}
		>
			{/* preventGrowOverflow off (here and on the row above): Mantine otherwise caps every child at an equal share of the
			    row, which squeezed the model trigger to a stub while the row still had room. */}
			<Group gap={4} wrap="nowrap" preventGrowOverflow={false} className={classes["controls"]} style={{ flex: 1, minWidth: 0 }}>
				<ModelSelectorCard
					modelOptions={modelOptions}
					cloudModelOptions={cloudModelOptions}
					selectedModel={selectedModel}
					disabled={modelSelectorDisabled || isSending || modelOptions.length === 0}
					onModelChange={onModelChange}
				/>
				{compact ? null : (
					<Menu position="top-start" offset={8} withinPortal={true} disabled={reasoningMenuDisabled}>
						<Menu.Target>
							<Tooltip label={reasoningLabel}>
								<ActionIcon
									size={36}
									variant={reasoningEnabled ? "light" : "subtle"}
									color={reasoningEnabled ? "primary" : "gray"}
									disabled={reasoningMenuDisabled}
									aria-label={reasoningLabel}
									data-testid="chat-reasoning-effort-menu-trigger"
								>
									<IconBrain size={15} />
								</ActionIcon>
							</Tooltip>
						</Menu.Target>
						<Menu.Dropdown>{reasoningItems}</Menu.Dropdown>
					</Menu>
				)}
				{showLocalToolControls && !compact ? (
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
				{/* The web tools are local tools, so their review mode is only meaningful while those are on. */}
				{showLocalToolControls && toolsEnabled ? (
					<WebContentAutoAcceptToggle
						conversationId={conversationId}
						disabled={disabled || isSending}
						onShownChange={onWebReviewShownChange}
					/>
				) : null}
				{showKnowledgeBaseControls && !compact ? (
					<DisabledReasonTooltip
						// Only the empty-corpus reason earns a keyboard stop; while sending the control is simply busy.
						controlDisabled={!knowledgeBaseHasDocuments}
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
					</DisabledReasonTooltip>
				) : null}
				{agentControlsAvailable ? (
					<AgentSelectorCard
						agentOptions={agentOptions}
						agentModeEnabled={agentModeEnabled}
						selectedAgentId={selectedAgentId}
						defaultOptionDisabled={conversationAgentBound}
						disabled={agentSelectorDisabled}
						onSelectAgent={onSelectAgent ?? (() => undefined)}
					/>
				) : null}
				{workflowSelector}
				{/* The ActionIcon below IS the labeled control; FileButton's own input is display:none and exists only to
				    open the OS picker. Marked hidden so it is not reported as an unnamed form control. */}
				{attachmentControlsAvailable ? (
					<FileButton
						onChange={onPickFiles}
						multiple={true}
						accept={attachmentAccept}
						inputProps={{ "aria-hidden": true, tabIndex: -1 }}
					>
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
				{overflowMenu}
				{showSamplingOptions && !compact ? (
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
						<ContextUsagePopover
							usage={contextUsage}
							// The send path's model rule: the local-default sentinel travels as no model, which the node resolves itself.
							modelName={toNodeChatRequestModel(selectedModel) ?? ""}
							// The send path's rule: an agent applies only in agent mode and while it is still listed.
							agentId={
								agentModeEnabled && agentOptions.some((agent) => agent.id === selectedAgentId) ? selectedAgentId : undefined
							}
							// The send path sends the toggle as is, so the estimate counts local tools only when it is on.
							useLocalTools={toolsEnabled}
						/>
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
						<ContextStateButton />
					</Group>
				) : null}
				{compact ? (
					<Group gap="xs" wrap="nowrap" ml="auto">
						{sendControls}
					</Group>
				) : null}
			</Group>
			{compact ? null : sendControls}
		</Group>
	);
}
