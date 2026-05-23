import { ActionIcon, Badge, Box, Group, Paper, ScrollArea, Stack, Text, TextInput, Tooltip } from "@mantine/core";
import { IconChevronLeft, IconChevronRight, IconMessage, IconPinnedFilled, IconPlus, IconSearch } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { ChatConversationModel } from "@/features/chat/models/ChatModels";

interface ConversationListProps {
	conversations: ChatConversationModel[];
	selectedConversationId?: string;
	collapsed?: boolean;
	disabled?: boolean;
	onCreateConversation: () => void;
	onSelect: (conversationId: string) => void;
	onToggleCollapse: () => void;
}

function formatRelative(iso?: string): string {
	if (!iso) {
		return "";
	}

	const date = new Date(iso);
	if (Number.isNaN(date.getTime())) {
		return "";
	}

	const now = new Date();
	const diffMin = Math.round((now.getTime() - date.getTime()) / 60000);
	if (diffMin < 1) {
		return "now";
	}
	if (diffMin < 60) {
		return `${diffMin}m`;
	}

	return date.toLocaleDateString(undefined, { month: "short", day: "numeric" });
}

function initials(title: string): string {
	const matches = title.match(/\b\w/g) ?? [];
	return matches.slice(0, 2).join("").toUpperCase() || title.slice(0, 2).toUpperCase();
}

export function ConversationList({
	conversations,
	selectedConversationId,
	collapsed = false,
	disabled = false,
	onCreateConversation,
	onSelect,
	onToggleCollapse,
}: ConversationListProps) {
	const { t } = useTranslation();
	const pinned = conversations.filter((conversation) => conversation.isPinned);
	const recent = conversations.filter((conversation) => !conversation.isPinned);
	const sections = [
		{ id: "pinned", title: t("pages.chat.conversationList.pinned", "Pinned"), items: pinned, icon: <IconPinnedFilled size={10} /> },
		{ id: "recent", title: t("pages.chat.conversationList.recent", "Recent"), items: recent, icon: undefined },
	];

	if (collapsed) {
		return (
			<Paper withBorder={true} h="100%" data-testid="conversation-list" style={{ display: "flex", flexDirection: "column", alignItems: "center", gap: 8, padding: 8 }}>
				<Tooltip label={t("pages.chat.conversationList.show", "Show conversations")} position="right">
					<ActionIcon variant="subtle" onClick={onToggleCollapse} aria-label={t("pages.chat.conversationList.expandAria", "Expand conversations")}>
						<IconChevronRight size={16} />
					</ActionIcon>
				</Tooltip>
				<ActionIcon variant="filled" color="dark" onClick={onCreateConversation} aria-label={t("pages.chat.newConversation", "New conversation")}>
					<IconPlus size={15} />
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

	return (
		<Paper withBorder={true} h="100%" data-testid="conversation-list" style={{ display: "flex", flexDirection: "column", minHeight: 0 }}>
			<Group justify="space-between" px="md" pt="md" pb="xs">
				<Text fw={700}>{t("pages.chat.conversations", "Conversations")}</Text>
				<Tooltip label={t("pages.chat.conversationList.hide", "Hide conversations")} position="left">
					<ActionIcon variant="subtle" onClick={onToggleCollapse} aria-label={t("pages.chat.conversationList.collapseAria", "Collapse conversations")}>
						<IconChevronLeft size={16} />
					</ActionIcon>
				</Tooltip>
			</Group>
			<Group gap={8} px="md" pb="xs" wrap="nowrap">
				<TextInput placeholder={t("pages.chat.conversationList.searchPlaceholder", "Search")} leftSection={<IconSearch size={14} />} size="xs" disabled={true} style={{ flex: 1 }} />
				<ActionIcon variant="filled" color="dark" size={30} radius="md" onClick={onCreateConversation} aria-label={t("pages.chat.newConversation", "New conversation")}>
					<IconPlus size={15} />
				</ActionIcon>
			</Group>
			<ScrollArea style={{ flex: 1, minHeight: 0 }} type="auto" px="xs">
				<Stack gap={2} pb="md">
					{sections.map((section) => (
						<Stack key={section.id} gap={2}>
							<Group gap={6} px="sm" py={6}>
								{section.icon}
								<Text size="xs" fw={700} c="dimmed" tt="uppercase">
									{section.title}
								</Text>
							</Group>
							{section.items.map((conversation) => (
								<Paper
									key={conversation.id}
									p="sm"
									radius="md"
									data-testid={`conversation-item-${conversation.id}`}
									onClick={() => {
										if (!disabled) {
											onSelect(conversation.id);
										}
									}}
									style={{
										cursor: disabled ? "not-allowed" : "pointer",
										background: conversation.id === selectedConversationId ? "var(--mantine-primary-color-light)" : "transparent",
									}}
								>
									<Stack gap={4}>
										<Group justify="space-between" wrap="nowrap" gap={8}>
											<Text fw={600} size="sm" lineClamp={1}>
												{conversation.title.trim() || t("pages.chat.conversationList.untitled", "Untitled")}
											</Text>
											<Text size="xs" c="dimmed">
												{formatRelative(conversation.lastActivity ?? conversation.updatedAt)}
											</Text>
										</Group>
										<Text size="xs" c="dimmed" lineClamp={1}>
											{conversation.lastMessagePreview?.trim() || t("pages.chat.noMessages", "No messages")}
										</Text>
										{conversation.isArchived ? (
											<Box>
												<Badge variant="light" color="gray" size="xs">
													{t("pages.chat.conversationList.archived", "Archived")}
												</Badge>
											</Box>
										) : null}
									</Stack>
								</Paper>
							))}
						</Stack>
					))}
				</Stack>
			</ScrollArea>
		</Paper>
	);
}
