import { Alert, Paper, Stack, Text } from "@mantine/core";
import { IconInfoCircle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { ChatInputArea } from "@/features/chat/components/ChatInputArea";
import { ChatMessageList } from "@/features/chat/components/ChatMessageList";
import { ConversationList } from "@/features/chat/components/ConversationList";
import { defaultChatUiCapabilities } from "@/features/chat/models/ChatCapabilityGates";
import type { ChatDisplayShellProps } from "@/features/chat/models/ChatModels";

export function ChatDisplayShell({
	conversations,
	selectedConversationId,
	modelOptions,
	selectedModel,
	reasoningEffort,
	availableReasoningEfforts,
	contextUsage,
	streamingMessage,
	timelineEntries = [],
	capabilities = defaultChatUiCapabilities,
	inputStatus,
	onSelectConversation,
	onCreateConversation,
	onToggleConversationList,
	onModelChange,
	onReasoningEffortChange,
	onSend,
	onCancel,
	conversationListCollapsed = false,
	disabledNotice,
}: ChatDisplayShellProps) {
	const { t } = useTranslation();
	const conversation = conversations.find((item) => item.id === selectedConversationId);

	return (
		<Stack gap="md" h="100%" mih={620}>
			<Alert color="blue" variant="light" icon={<IconInfoCircle size={16} />} data-testid="chat-capability-notice">
				{disabledNotice ?? t("pages.chat.phase41Notice", "Display preview only. Sending and model changes are disabled until the local chat adapter is wired.")}
			</Alert>
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
				<ConversationList
					conversations={conversations}
					selectedConversationId={selectedConversationId}
					collapsed={conversationListCollapsed}
					disabled={inputStatus.chatInputDisabled}
					onCreateConversation={onCreateConversation}
					onSelect={onSelectConversation}
					onToggleCollapse={onToggleConversationList}
				/>
				<Paper
					withBorder={true}
					p="md"
					h="100%"
					style={{ display: "flex", flexDirection: "column", minHeight: 0, minWidth: 0, borderRadius: "0 var(--mantine-radius-md) var(--mantine-radius-md) 0" }}
				>
					<Stack gap="md" style={{ flex: 1, minHeight: 0 }}>
							<Stack gap={4}>
								<Text fw={700} data-testid="chat-window-title" style={{ flex: 1, minWidth: 0 }} lineClamp={1}>
									{conversation?.title?.trim() || t("pages.chat.windowTitle", "Local chat")}
								</Text>
								<Text size="xs" c="dimmed">
									{t("pages.chat.localPreviewSubtitle", "Local node display shell — safe mock data")}
								</Text>
							</Stack>
							<ChatMessageList conversation={conversation} streamingMessage={streamingMessage} timelineEntries={timelineEntries} />
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
								onCancel={onCancel}
								onModelChange={onModelChange}
								onReasoningEffortChange={onReasoningEffortChange}
								onSend={onSend}
							/>
					</Stack>
				</Paper>
			</div>
		</Stack>
	);
}
