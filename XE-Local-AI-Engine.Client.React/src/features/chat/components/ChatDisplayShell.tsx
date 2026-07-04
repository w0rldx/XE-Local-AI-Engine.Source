import { ActionIcon, Alert, Box, Drawer, Group, Paper, Stack, Switch, Text, Tooltip } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconInfoCircle, IconLayoutSidebar, IconUpload } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import { ChatInputArea } from "@/features/chat/components/ChatInputArea";
import { ChatMessageList } from "@/features/chat/components/ChatMessageList";
import { ConversationList } from "@/features/chat/components/ConversationList";
import { usePaneFileDrop } from "@/features/chat/hooks/usePaneFileDrop";
import { defaultChatUiCapabilities } from "@/features/chat/models/ChatCapabilityGates";
import type { ChatDisplayShellProps } from "@/features/chat/models/ChatModels";

const EMPTY_TIMELINE_ENTRIES: ChatDisplayShellProps["timelineEntries"] = [];
// Stable empty default for the optional agentOptions prop — a fresh `[]` default would allocate a new array
// reference every render (same reasoning as EMPTY_TIMELINE_ENTRIES above).
const EMPTY_AGENT_OPTIONS: ChatDisplayShellProps["agentOptions"] = [];

/* eslint-disable react-doctor/no-inline-exhaustive-style */

export function ChatDisplayShell({
	conversations,
	selectedConversationId,
	modelOptions,
	cloudModelOptions,
	selectedModel,
	reasoningEffort,
	availableReasoningEfforts,
	activeModelToolCapable = false,
	toolsEnabled = false,
	contextUsage,
	streamingMessage,
	timelineEntries = EMPTY_TIMELINE_ENTRIES,
	capabilities = defaultChatUiCapabilities,
	inputStatus,
	conversationSearchQuery,
	showArchivedConversations,
	mutatingConversationId,
	onSelectConversation,
	onCreateConversation,
	onToggleConversationList,
	onModelChange,
	onReasoningEffortChange,
	onToggleTools,
	agentControlsAvailable = false,
	agentModeEnabled = false,
	selectedAgentId = "",
	agentOptions = EMPTY_AGENT_OPTIONS,
	onSelectAgent,
	attachments,
	pendingUploads,
	onUploadFiles,
	onRemoveAttachment,
	onSend,
	onCancel,
	onRegenerate,
	onConversationSearchChange,
	onToggleShowArchivedConversations,
	onRenameConversation,
	onToggleConversationPinned,
	onToggleConversationArchived,
	boundAgentMemoryEnabled = false,
	onToggleConversationMemoryExcluded,
	onDeleteConversation,
	onBranchFromMessage,
	activeRevisionByGroup,
	onSelectRevision,
	feedbackByMessageId,
	pendingFeedbackMessageId,
	onSubmitFeedback,
	conversationListCollapsed = false,
	disabledNotice,
	isLoadingMessages = false,
}: ChatDisplayShellProps) {
	const { t } = useTranslation();
	const conversation = conversations.find((item) => item.id === selectedConversationId);
	// The app shell shows a persistent 220px sidebar from width 768 up (Layout.tsx's own useWindowDimensions
	// check), so the two-pane grid here (320px conversation list + chat pane) doesn't have room to breathe until
	// the window is wider than roughly 220 + 320 + ~480 usable chat pane ≈ 1024px. Below that, collapse to a
	// single full-width column and move the conversation list into an off-canvas Drawer toggled from the header.
	// useWindowDimensions (unlike useMediaQuery) reads window.innerWidth synchronously on first render, so there
	// is no undefined-value flash of the desktop grid before it collapses. jsdom defaults innerWidth to 1024, so
	// `< 1024` (not `<=`) keeps desktop the default under the existing tests.
	const { width } = useWindowDimensions();
	const isMobile = width < 1024;
	const [conversationDrawerOpened, { open: openConversationDrawer, close: closeConversationDrawer }] = useDisclosure(false);

	// Pane-level file drop: a user can drop files anywhere on the chat window (message list + composer), not only on the
	// composer's paperclip. Gated by the same capability + wired handler as the composer, and suppressed while the input
	// is disabled / mid-send. Drops onto the composer itself are handled there (it stops propagation), so this never
	// double-fires for the same drop.
	const fileDropEnabled =
		capabilities.showFileAttachmentControls && Boolean(onUploadFiles) && !inputStatus.chatInputDisabled && !inputStatus.isSending;
	const { isFileDragActive, dropProps } = usePaneFileDrop(fileDropEnabled, (files) => onUploadFiles?.(files));

	const conversationList = (
		<ConversationList
			conversations={conversations}
			selectedConversationId={selectedConversationId}
			collapsed={isMobile ? false : conversationListCollapsed}
			embedded={isMobile}
			disabled={inputStatus.chatInputDisabled}
			searchQuery={conversationSearchQuery}
			showArchived={showArchivedConversations}
			mutatingConversationId={mutatingConversationId}
			onCreateConversation={onCreateConversation}
			onSelect={(conversationId) => {
				onSelectConversation(conversationId);
				if (isMobile) {
					closeConversationDrawer();
				}
			}}
			onToggleCollapse={onToggleConversationList}
			onSearchChange={onConversationSearchChange}
			onToggleShowArchived={onToggleShowArchivedConversations}
			onRename={onRenameConversation}
			onTogglePin={onToggleConversationPinned}
			onToggleArchive={onToggleConversationArchived}
			onDelete={onDeleteConversation}
		/>
	);

	const chatPaneHeader = (
		<Stack gap={4}>
			<Group gap="xs" wrap="nowrap" align="center">
				{isMobile ? (
					<Tooltip label={t("pages.chat.conversationList.show", "Show conversations")}>
						<ActionIcon
							variant="subtle"
							onClick={openConversationDrawer}
							aria-label={t("pages.chat.conversationList.show", "Show conversations")}
							data-testid="chat-conversations-toggle"
						>
							<IconLayoutSidebar size={18} />
						</ActionIcon>
					</Tooltip>
				) : null}
				<Text fw={700} data-testid="chat-window-title" style={{ flex: 1, minWidth: 0 }} lineClamp={1}>
					{conversation?.title?.trim() || t("pages.chat.windowTitle", "Local chat")}
				</Text>
				{/* Temporary-chat toggle: shown only when the bound agent has adaptive memory enabled. A temporary chat
				    still USES existing memory; it just won't teach the agent new memory from this thread. The toggle
				    needs a persisted conversation to PATCH, so it is suppressed until one is selected. */}
				{boundAgentMemoryEnabled && onToggleConversationMemoryExcluded && conversation ? (
					<Tooltip
						label={t(
							"pages.chat.temporaryChat.tooltip",
							"This chat won't teach the agent new memory; it still uses existing memory.",
						)}
						withArrow={true}
						multiline={true}
						maw={240}
					>
						<Switch
							size="xs"
							labelPosition="left"
							label={t("pages.chat.temporaryChat.label", "Temporary chat")}
							checked={conversation.memoryExcluded ?? false}
							onChange={(event) => onToggleConversationMemoryExcluded(conversation.id, event.currentTarget.checked)}
							data-testid="chat-temporary-toggle"
						/>
					</Tooltip>
				) : null}
			</Group>
		</Stack>
	);

	const chatPane = (
		<Paper
			withBorder={true}
			p="md"
			h="100%"
			data-testid="chat-pane"
			{...dropProps}
			style={{
				position: "relative",
				display: "flex",
				flexDirection: "column",
				minHeight: 0,
				minWidth: 0,
				borderRadius: isMobile ? "var(--mantine-radius-md)" : "0 var(--mantine-radius-md) var(--mantine-radius-md) 0",
			}}
		>
			{isFileDragActive ? (
				<Box
					data-testid="chat-file-drop-overlay"
					style={{
						position: "absolute",
						inset: 0,
						zIndex: 5,
						display: "flex",
						alignItems: "center",
						justifyContent: "center",
						borderRadius: "inherit",
						border: "2px dashed var(--mantine-color-primary-5)",
						backgroundColor: "var(--mantine-color-body)",
						opacity: 0.92,
						// Let drag/drop events fall through to the Paper underneath so the overlay never swallows the drop.
						pointerEvents: "none",
					}}
				>
					<Group gap="xs" c="primary">
						<IconUpload size={20} />
						<Text fw={600}>{t("pages.chat.composer.dropToAttach", "Drop files to attach")}</Text>
					</Group>
				</Box>
			) : null}
			<Stack gap="md" style={{ flex: 1, minHeight: 0 }}>
				{chatPaneHeader}
				<ChatMessageList
					conversation={conversation}
					streamingMessage={streamingMessage}
					timelineEntries={timelineEntries}
					onRegenerate={onRegenerate}
					onBranch={onBranchFromMessage}
					activeRevisionByGroup={activeRevisionByGroup}
					onSelectRevision={onSelectRevision}
					showFeedbackControls={capabilities.showConversationFeedbackControls}
					feedbackByMessageId={feedbackByMessageId}
					pendingFeedbackMessageId={pendingFeedbackMessageId}
					onSubmitFeedback={onSubmitFeedback}
					isLoadingMessages={isLoadingMessages}
					reasoningEffort={reasoningEffort}
				/>
				<ChatInputArea
					availableReasoningEfforts={availableReasoningEfforts}
					capabilities={capabilities}
					contextUsage={contextUsage}
					disabled={inputStatus.chatInputDisabled}
					isSending={inputStatus.isSending}
					modelOptions={modelOptions}
					cloudModelOptions={cloudModelOptions}
					modelSelectorDisabled={inputStatus.modelSelectorDisabled}
					sendDisabled={inputStatus.sendDisabled}
					selectedModel={selectedModel}
					reasoningEffort={reasoningEffort}
					activeModelToolCapable={activeModelToolCapable}
					toolsEnabled={toolsEnabled}
					agentControlsAvailable={agentControlsAvailable}
					agentModeEnabled={agentModeEnabled}
					selectedAgentId={selectedAgentId}
					agentOptions={agentOptions}
					attachments={attachments}
					pendingUploads={pendingUploads}
					onUploadFiles={onUploadFiles}
					onRemoveAttachment={onRemoveAttachment}
					onCancel={onCancel}
					onModelChange={onModelChange}
					onReasoningEffortChange={onReasoningEffortChange}
					onToggleTools={onToggleTools}
					onSelectAgent={onSelectAgent}
					onSend={onSend}
				/>
			</Stack>
		</Paper>
	);

	const notice = disabledNotice ? (
		<Alert color="blue" variant="light" icon={<IconInfoCircle size={16} />} data-testid="chat-capability-notice">
			{disabledNotice}
		</Alert>
	) : null;

	if (isMobile) {
		return (
			<Stack gap="md" h="100%">
				{notice}
				<div style={{ flex: 1, minHeight: 0 }}>{chatPane}</div>
				<Drawer
					opened={conversationDrawerOpened}
					onClose={closeConversationDrawer}
					position="left"
					size="85%"
					padding={0}
					withCloseButton={true}
					title={t("pages.chat.conversations", "Conversations")}
					data-testid="chat-conversations-drawer"
					// Body padding is 0 so the embedded list controls their own md inset; pad the header inline to match
					// so the drawer title lines up with the search field + conversation rows below it instead of flush-left.
					styles={{ header: { paddingInline: "var(--mantine-spacing-md)" } }}
				>
					{conversationList}
				</Drawer>
			</Stack>
		);
	}

	return (
		// mih is a floor, not a fixed height (h="100%" above governs normal sizing) — it exists so the two-pane
		// grid doesn't collapse below a usable size, but a flat 620px overflows short/landscape desktop windows.
		// min() caps it at the viewport height (minus a small allowance for whatever sits above this shell).
		<Stack gap="md" h="100%" mih="min(620px, calc(100dvh - 96px))">
			{notice}
			<div
				style={{
					position: "relative",
					display: "grid",
					gridTemplateColumns: conversationListCollapsed ? "68px minmax(0, 1fr)" : "320px minmax(0, 1fr)",
					gridTemplateRows: "minmax(0, 1fr)",
					gap: "var(--mantine-spacing-md)",
					flex: 1,
					minHeight: 0,
					transition: "grid-template-columns 240ms cubic-bezier(0.4, 0, 0.2, 1)",
				}}
			>
				{conversationList}
				{chatPane}
			</div>
		</Stack>
	);
}
