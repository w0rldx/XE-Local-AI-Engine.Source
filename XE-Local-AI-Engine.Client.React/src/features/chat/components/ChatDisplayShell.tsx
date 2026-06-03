import { ActionIcon, Alert, Drawer, Group, Paper, Stack, Text, Tooltip } from "@mantine/core";
import { useDisclosure, useMediaQuery } from "@mantine/hooks";
import { IconInfoCircle, IconLayoutSidebar } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { ChatInputArea } from "@/features/chat/components/ChatInputArea";
import { ChatMessageList } from "@/features/chat/components/ChatMessageList";
import { ConversationList } from "@/features/chat/components/ConversationList";
import { defaultChatUiCapabilities } from "@/features/chat/models/ChatCapabilityGates";
import type { ChatDisplayShellProps } from "@/features/chat/models/ChatModels";

const EMPTY_TIMELINE_ENTRIES: ChatDisplayShellProps["timelineEntries"] = [];

/* eslint-disable react-doctor/no-inline-exhaustive-style */

export function ChatDisplayShell({
	conversations,
	selectedConversationId,
	modelOptions,
	selectedModel,
	reasoningEffort,
	availableReasoningEfforts,
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
	agentOptions = [],
	onSelectAgent,
	onSend,
	onCancel,
	onRegenerate,
	onConversationSearchChange,
	onToggleShowArchivedConversations,
	onRenameConversation,
	onToggleConversationPinned,
	onToggleConversationArchived,
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
	// Below the md breakpoint (mirrors Layout.tsx / useWindowDimensions' 768 cutoff) the two-pane grid is
	// unusable — the 320px list squeezes the chat pane to a sliver. Switch to a single full-width column and
	// move the conversation list into an off-canvas Drawer toggled from the header. useMediaQuery is undefined
	// on the server / first synchronous render and in jsdom's mocked matchMedia (matches: false), so desktop
	// is the default and the existing desktop tests keep exercising the grid path unchanged.
	const isMobile = useMediaQuery("(max-width: 767px)");
	const [conversationDrawerOpened, { open: openConversationDrawer, close: closeConversationDrawer }] = useDisclosure(false);

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
			</Group>
		</Stack>
	);

	const chatPane = (
		<Paper
			withBorder={true}
			p="md"
			h="100%"
			style={{
				display: "flex",
				flexDirection: "column",
				minHeight: 0,
				minWidth: 0,
				borderRadius: isMobile
					? "var(--mantine-radius-md)"
					: "0 var(--mantine-radius-md) var(--mantine-radius-md) 0",
			}}
		>
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
					modelSelectorDisabled={inputStatus.modelSelectorDisabled}
					sendDisabled={inputStatus.sendDisabled}
					selectedModel={selectedModel}
					reasoningEffort={reasoningEffort}
					toolsEnabled={toolsEnabled}
					agentControlsAvailable={agentControlsAvailable}
					agentModeEnabled={agentModeEnabled}
					selectedAgentId={selectedAgentId}
					agentOptions={agentOptions}
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
		<Stack gap="md" h="100%" mih={620}>
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
