import { ActionIcon, Badge, Group, Menu, Paper, ScrollArea, Stack, Switch, Text, TextInput, Tooltip } from "@mantine/core";
import {
	IconArchive,
	IconArchiveOff,
	IconChevronLeft,
	IconChevronRight,
	IconDots,
	IconMessage,
	IconPencil,
	IconPin,
	IconPinned,
	IconPinnedFilled,
	IconPlus,
	IconSearch,
	IconTrash,
} from "@tabler/icons-react";
import { type KeyboardEvent, type MouseEvent, useState } from "react";
import { useTranslation } from "react-i18next";

import type { ChatConversationModel } from "@/features/chat/models/ChatModels";

/* eslint-disable react-doctor/no-giant-component */

interface ConversationListProps {
	conversations: ChatConversationModel[];
	selectedConversationId?: string;
	collapsed?: boolean;
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

function matchesQuery(conversation: ChatConversationModel, query: string): boolean {
	if (query.length === 0) {
		return true;
	}

	const haystack = `${conversation.title} ${conversation.lastMessagePreview ?? ""}`.toLowerCase();
	return haystack.includes(query);
}

export function ConversationList({
	conversations,
	selectedConversationId,
	collapsed = false,
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
					onClick={onCreateConversation}
					aria-label={t("pages.chat.newConversation", "New conversation")}
				>
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
		<Paper
			withBorder={true}
			h="100%"
			data-testid="conversation-list"
			style={{ display: "flex", flexDirection: "column", minHeight: 0 }}
		>
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
			<Group gap={8} px="md" pb="xs" wrap="nowrap">
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
								{section.items.map((conversation) => {
									const isRemote = conversation.origin === "remote";
									const isRenaming = renamingId === conversation.id;
									const isMutating = mutatingConversationId === conversation.id;
									const canManage =
										!isRemote && (Boolean(onRename) || Boolean(onTogglePin) || Boolean(onToggleArchive) || Boolean(onDelete));

									return (
										<Paper
											key={conversation.id}
											p="sm"
											radius="md"
											data-testid={`conversation-item-${conversation.id}`}
											onClick={() => {
												if (!disabled && !isRenaming) {
													onSelect(conversation.id);
												}
											}}
											style={{
												cursor: disabled || isRenaming ? "default" : "pointer",
												background:
													conversation.id === selectedConversationId ? "var(--mantine-primary-color-light)" : "transparent",
											}}
										>
											<Stack gap={4}>
												<Group justify="space-between" wrap="nowrap" gap={8}>
													{isRenaming ? (
														<TextInput
															size="xs"
															value={renameDraft}
															autoFocus={true}
															onClick={(event) => event.stopPropagation()}
															onChange={(event) => setRenameDraft(event.currentTarget.value)}
															onKeyDown={(event) => handleRenameKeyDown(event, conversation.id)}
															onBlur={() => commitRename(conversation.id)}
															style={{ flex: 1 }}
															data-testid={`conversation-rename-input-${conversation.id}`}
															aria-label={t("pages.chat.conversationList.renameAria", "Rename conversation")}
														/>
													) : (
														<Text fw={600} size="sm" lineClamp={1} style={{ flex: 1, minWidth: 0 }}>
															{conversation.title.trim() || t("pages.chat.conversationList.untitled", "Untitled")}
														</Text>
													)}
													{isRenaming ? null : (
														<Group gap={4} wrap="nowrap">
															<Text size="xs" c="dimmed">
																{formatRelative(conversation.lastActivity ?? conversation.updatedAt)}
															</Text>
															{canManage ? (
																<Menu position="bottom-end" withinPortal={true} disabled={isMutating}>
																	<Menu.Target>
																		<ActionIcon
																			variant="subtle"
																			color="gray"
																			size="sm"
																			loading={isMutating}
																			onClick={(event) => event.stopPropagation()}
																			aria-label={t("pages.chat.conversationList.actionsAria", "Conversation actions")}
																			data-testid={`conversation-actions-${conversation.id}`}
																		>
																			<IconDots size={14} />
																		</ActionIcon>
																	</Menu.Target>
																	<Menu.Dropdown onClick={(event) => event.stopPropagation()}>
																		{onRename ? (
																			<Menu.Item
																				leftSection={<IconPencil size={14} />}
																				onClick={() => beginRename(conversation)}
																				data-testid={`conversation-rename-${conversation.id}`}
																			>
																				{t("pages.chat.conversationList.rename", "Rename")}
																			</Menu.Item>
																		) : null}
																		{onTogglePin ? (
																			<Menu.Item
																				leftSection={conversation.isPinned ? <IconPinned size={14} /> : <IconPin size={14} />}
																				onClick={() => onTogglePin(conversation.id, !conversation.isPinned)}
																				data-testid={`conversation-pin-${conversation.id}`}
																			>
																				{conversation.isPinned
																					? t("pages.chat.conversationList.unpin", "Unpin")
																					: t("pages.chat.conversationList.pin", "Pin")}
																			</Menu.Item>
																		) : null}
																		{onToggleArchive ? (
																			<Menu.Item
																				leftSection={
																					conversation.isArchived ? <IconArchiveOff size={14} /> : <IconArchive size={14} />
																				}
																				onClick={() => onToggleArchive(conversation.id, !conversation.isArchived)}
																				data-testid={`conversation-archive-${conversation.id}`}
																			>
																				{conversation.isArchived
																					? t("pages.chat.conversationList.unarchive", "Unarchive")
																					: t("pages.chat.conversationList.archive", "Archive")}
																			</Menu.Item>
																		) : null}
																		{onDelete ? (
																			<Tooltip
																				label={t(
																					"pages.chat.conversationList.deleteShiftHint",
																					"Shift-click to skip confirmation",
																				)}
																				position="left"
																				withArrow={true}
																				openDelay={300}
																			>
																				<Menu.Item
																					color="red"
																					leftSection={<IconTrash size={14} />}
																					onClick={(event: MouseEvent<HTMLButtonElement>) =>
																						onDelete(conversation.id, event.shiftKey)
																					}
																					data-testid={`conversation-delete-${conversation.id}`}
																				>
																					{t("pages.chat.conversationList.delete", "Delete")}
																				</Menu.Item>
																			</Tooltip>
																		) : null}
																	</Menu.Dropdown>
																</Menu>
															) : null}
														</Group>
													)}
												</Group>
												<Text size="xs" c="dimmed" lineClamp={1}>
													{conversation.lastMessagePreview?.trim() || t("pages.chat.noMessages", "No messages")}
												</Text>
												{isRemote || conversation.isArchived ? (
													<Group gap={4}>
														{isRemote ? (
															<Tooltip
																label={t(
																	"pages.chat.conversationList.remoteTooltip",
																	"Started from a paired client. View-only on this node.",
																)}
																withArrow={true}
															>
																<Badge
																	variant="light"
																	color="blue"
																	size="xs"
																	data-testid={`conversation-remote-badge-${conversation.id}`}
																>
																	{t("pages.chat.conversationList.remote", "Remote")}
																</Badge>
															</Tooltip>
														) : null}
														{conversation.isArchived ? (
															<Badge variant="light" color="gray" size="xs">
																{t("pages.chat.conversationList.archived", "Archived")}
															</Badge>
														) : null}
													</Group>
												) : null}
											</Stack>
										</Paper>
									);
								})}
							</Stack>
						),
					)}
				</Stack>
			</ScrollArea>
		</Paper>
	);
}
