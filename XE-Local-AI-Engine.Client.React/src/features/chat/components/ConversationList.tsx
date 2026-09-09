import { ActionIcon, Group, Paper, ScrollArea, Stack, Switch, Text, TextInput, Tooltip } from "@mantine/core";
import { IconArchive, IconChevronLeft, IconPinnedFilled, IconPlus, IconSearch } from "@tabler/icons-react";
import { type KeyboardEvent, memo, useState } from "react";
import { useTranslation } from "react-i18next";

import { ConversationListCollapsedRail } from "@/features/chat/components/ConversationList/ConversationListCollapsedRail";
import { ConversationListItem } from "@/features/chat/components/ConversationList/ConversationListItem";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";

/* eslint-disable react-doctor/no-giant-component -- The list and its row menus share selection, mutation, and responsive-collapse state; splitting them would duplicate that coordination. */

interface ConversationListProps {
	conversations: ChatConversationModel[];
	selectedConversationId?: string;
	collapsed?: boolean;
	// Rendered inside the mobile Drawer, which already supplies the "Conversations" title + close button. Drops this
	// component's own bordered card + header (title + collapse toggle) so they are not duplicated.
	embedded?: boolean;
	disabled?: boolean;
	searchQuery?: string;
	showArchived?: boolean;
	mutatingConversationId?: string;
	onCreateConversation: () => void;
	onSelect: (conversationId: string) => void;
	onToggleCollapse: () => void;
	onSearchChange?: (query: string) => void;
	onToggleShowArchived?: (showArchived: boolean) => void;
	onRename?: (conversationId: string, title: string) => void;
	onTogglePin?: (conversationId: string, isPinned: boolean) => void;
	onToggleArchive?: (conversationId: string, archived: boolean) => void;
	onDelete?: (conversationId: string, skipConfirm: boolean) => void;
}

function matchesQuery(conversation: ChatConversationModel, query: string): boolean {
	if (query.length === 0) {
		return true;
	}

	const haystack = `${conversation.title} ${conversation.lastMessagePreview ?? ""}`.toLowerCase();
	return haystack.includes(query);
}

// Memoized: the parent re-folds the conversation array on every streaming token (the message pane reads the live
// conversation from it), but the sidebar renders only summaries. Callers pass a reference-stable summary array +
// stable callbacks, so an unchanged sidebar skips the whole row/menu subtree during generation.
export const ConversationList = memo(function ConversationList({
	conversations,
	selectedConversationId,
	collapsed = false,
	embedded = false,
	disabled = false,
	searchQuery = "",
	showArchived = false,
	mutatingConversationId,
	onCreateConversation,
	onSelect,
	onToggleCollapse,
	onSearchChange,
	onToggleShowArchived,
	onRename,
	onTogglePin,
	onToggleArchive,
	onDelete,
}: ConversationListProps) {
	const { t } = useTranslation();
	const [renamingId, setRenamingId] = useState<string | undefined>();
	const [renameDraft, setRenameDraft] = useState("");

	const normalizedQuery = searchQuery.trim().toLowerCase();
	// Archived conversations only surface when the operator opts in; search always narrows the visible set.
	const visible = conversations.filter(
		(conversation) => (showArchived || !conversation.isArchived) && matchesQuery(conversation, normalizedQuery),
	);
	const pinned = visible.filter((conversation) => conversation.isPinned && !conversation.isArchived);
	const recent = visible.filter((conversation) => !conversation.isPinned && !conversation.isArchived);
	const archived = visible.filter((conversation) => conversation.isArchived);
	const sections = [
		{
			id: "pinned",
			title: t("pages.chat.conversationList.pinned", "Pinned"),
			items: pinned,
			icon: <IconPinnedFilled size={10} />,
		},
		{ id: "recent", title: t("pages.chat.conversationList.recent", "Recent"), items: recent, icon: undefined },
		{
			id: "archived",
			title: t("pages.chat.conversationList.archived", "Archived"),
			items: archived,
			icon: <IconArchive size={10} />,
		},
	];

	const beginRename = (conversation: ChatConversationModel): void => {
		setRenamingId(conversation.id);
		setRenameDraft(conversation.title.trim());
	};

	const cancelRename = (): void => {
		setRenamingId(undefined);
		setRenameDraft("");
	};

	const commitRename = (conversationId: string): void => {
		const trimmed = renameDraft.trim();
		if (trimmed.length > 0) {
			onRename?.(conversationId, trimmed);
		}
		cancelRename();
	};

	const handleRenameKeyDown = (event: KeyboardEvent<HTMLInputElement>, conversationId: string): void => {
		if (event.key === "Enter") {
			event.preventDefault();
			commitRename(conversationId);
		} else if (event.key === "Escape") {
			event.preventDefault();
			cancelRename();
		}
	};

	if (collapsed) {
		return (
			<ConversationListCollapsedRail
				conversations={conversations}
				selectedConversationId={selectedConversationId}
				disabled={disabled}
				onCreateConversation={onCreateConversation}
				onSelect={onSelect}
				onToggleCollapse={onToggleCollapse}
			/>
		);
	}

	return (
		<Paper
			withBorder={!embedded}
			radius={embedded ? 0 : undefined}
			h="100%"
			data-testid="conversation-list"
			style={{ display: "flex", flexDirection: "column", minHeight: 0, background: embedded ? "transparent" : undefined }}
		>
			{embedded ? null : (
				<Group justify="space-between" px="md" pt="md" pb="xs">
					<Text fw={700}>{t("pages.chat.conversations", "Conversations")}</Text>
					<Tooltip label={t("pages.chat.conversationList.hide", "Hide conversations")} position="left">
						<ActionIcon
							variant="subtle"
							onClick={onToggleCollapse}
							aria-label={t("pages.chat.conversationList.collapseAria", "Collapse conversations")}
						>
							<IconChevronLeft size={16} />
						</ActionIcon>
					</Tooltip>
				</Group>
			)}
			<Group gap={8} px="md" pt={embedded ? "sm" : undefined} pb="xs" wrap="nowrap">
				<TextInput
					placeholder={t("pages.chat.conversationList.searchPlaceholder", "Search")}
					leftSection={<IconSearch size={14} />}
					size="xs"
					value={searchQuery}
					onChange={(event) => onSearchChange?.(event.currentTarget.value)}
					disabled={!onSearchChange}
					style={{ flex: 1 }}
					data-testid="conversation-search"
					aria-label={t("pages.chat.conversationList.searchAria", "Search conversations")}
				/>
				<ActionIcon
					variant="filled"
					color="dark"
					size={30}
					radius="md"
					onClick={onCreateConversation}
					aria-label={t("pages.chat.newConversation", "New conversation")}
				>
					<IconPlus size={15} />
				</ActionIcon>
			</Group>
			{onToggleShowArchived ? (
				<Group px="md" pb="xs">
					<Switch
						size="xs"
						checked={showArchived}
						onChange={(event) => onToggleShowArchived(event.currentTarget.checked)}
						label={t("pages.chat.conversationList.showArchived", "Show archived")}
						data-testid="conversation-show-archived"
					/>
				</Group>
			) : null}
			<ScrollArea style={{ flex: 1, minHeight: 0 }} type="auto" px="xs">
				<Stack gap={2} pb="md">
					{visible.length === 0 ? (
						<Text size="xs" c="dimmed" px="sm" py="md" data-testid="conversation-list-empty">
							{normalizedQuery.length > 0
								? t("pages.chat.conversationList.noMatches", "No conversations match your search.")
								: t("pages.chat.conversationList.empty", "No conversations yet.")}
						</Text>
					) : null}
					{sections.map((section) =>
						section.items.length === 0 ? null : (
							<Stack key={section.id} gap={2}>
								<Group gap={6} px="sm" py={6}>
									{section.icon}
									<Text size="xs" fw={700} c="dimmed" tt="uppercase">
										{section.title}
									</Text>
								</Group>
								{section.items.map((conversation) => (
									<ConversationListItem
										key={conversation.id}
										conversation={conversation}
										selected={conversation.id === selectedConversationId}
										disabled={disabled}
										isRenaming={renamingId === conversation.id}
										renameDraft={renameDraft}
										isMutating={mutatingConversationId === conversation.id}
										onSelect={onSelect}
										onBeginRename={beginRename}
										onRenameDraftChange={(event) => setRenameDraft(event.currentTarget.value)}
										onRenameKeyDown={handleRenameKeyDown}
										onCommitRename={commitRename}
										onRename={onRename}
										onTogglePin={onTogglePin}
										onToggleArchive={onToggleArchive}
										onDelete={onDelete}
									/>
								))}
							</Stack>
						),
					)}
				</Stack>
			</ScrollArea>
		</Paper>
	);
});
