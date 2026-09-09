import { ActionIcon, Paper, ScrollArea, Stack, Tooltip } from "@mantine/core";
import { IconChevronRight, IconMessage, IconPlus } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { ChatConversationModel } from "@/features/chat/models/ChatModels";

function initials(title: string): string {
	const matches = title.match(/\b\w/g) ?? [];
	return matches.slice(0, 2).join("").toUpperCase() || title.slice(0, 2).toUpperCase();
}

interface ConversationListCollapsedRailProps {
	conversations: ChatConversationModel[];
	selectedConversationId?: string;
	disabled: boolean;
	onCreateConversation: () => void;
	onSelect: (conversationId: string) => void;
	onToggleCollapse: () => void;
}

export function ConversationListCollapsedRail({
	conversations,
	selectedConversationId,
	disabled,
	onCreateConversation,
	onSelect,
	onToggleCollapse,
}: ConversationListCollapsedRailProps) {
	const { t } = useTranslation();

	return (
		<Paper
			withBorder={true}
			h="100%"
			data-testid="conversation-list"
			style={{ display: "flex", flexDirection: "column", alignItems: "center", gap: 8, padding: 8 }}
		>
			<Tooltip label={t("pages.chat.conversationList.show", "Show conversations")} position="right">
				<ActionIcon
					variant="subtle"
					onClick={onToggleCollapse}
					aria-label={t("pages.chat.conversationList.expandAria", "Expand conversations")}
				>
					<IconChevronRight size={16} />
				</ActionIcon>
			</Tooltip>
			<ActionIcon
				variant="filled"
				color="dark"
				size={40}
				radius="md"
				onClick={onCreateConversation}
				aria-label={t("pages.chat.newConversation", "New conversation")}
			>
				<IconPlus size={16} />
			</ActionIcon>
			<ScrollArea style={{ flex: 1, width: "100%", minHeight: 0 }} type="auto">
				<Stack gap={6} align="center">
					{conversations.map((conversation) => {
						const label = conversation.title.trim() || t("pages.chat.conversationList.untitled", "Untitled");
						return (
							<Tooltip key={conversation.id} label={label} position="right" withArrow={true}>
								<ActionIcon
									variant={conversation.id === selectedConversationId ? "filled" : "light"}
									color={conversation.id === selectedConversationId ? "primary" : "gray"}
									size={40}
									radius="md"
									disabled={disabled}
									onClick={() => onSelect(conversation.id)}
									aria-label={label}
									data-testid={`conversation-item-${conversation.id}`}
								>
									{conversation.isPinned ? initials(label) : <IconMessage size={16} />}
								</ActionIcon>
							</Tooltip>
						);
					})}
				</Stack>
			</ScrollArea>
		</Paper>
	);
}
