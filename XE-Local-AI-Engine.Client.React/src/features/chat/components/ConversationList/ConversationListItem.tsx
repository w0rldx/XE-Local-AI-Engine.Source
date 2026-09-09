import { ActionIcon, Badge, Group, Menu, Paper, Stack, Text, TextInput, Tooltip } from "@mantine/core";
import { IconArchive, IconArchiveOff, IconDots, IconPencil, IconPin, IconPinned, IconTrash } from "@tabler/icons-react";
import type { ChangeEvent, KeyboardEvent, MouseEvent } from "react";
import { useTranslation } from "react-i18next";

import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";

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

	return formatTimestamp(date.getTime(), { month: "short", day: "numeric" });
}

interface ConversationListItemProps {
	conversation: ChatConversationModel;
	selected: boolean;
	disabled: boolean;
	isRenaming: boolean;
	renameDraft: string;
	isMutating: boolean;
	onSelect: (conversationId: string) => void;
	onBeginRename: (conversation: ChatConversationModel) => void;
	onRenameDraftChange: (event: ChangeEvent<HTMLInputElement>) => void;
	onRenameKeyDown: (event: KeyboardEvent<HTMLInputElement>, conversationId: string) => void;
	onCommitRename: (conversationId: string) => void;
	onRename?: (conversationId: string, title: string) => void;
	onTogglePin?: (conversationId: string, isPinned: boolean) => void;
	onToggleArchive?: (conversationId: string, archived: boolean) => void;
	onDelete?: (conversationId: string, skipConfirm: boolean) => void;
}

export function ConversationListItem({
	conversation,
	selected,
	disabled,
	isRenaming,
	renameDraft,
	isMutating,
	onSelect,
	onBeginRename,
	onRenameDraftChange,
	onRenameKeyDown,
	onCommitRename,
	onRename,
	onTogglePin,
	onToggleArchive,
	onDelete,
}: ConversationListItemProps) {
	const { t } = useTranslation();
	const isRemote = conversation.origin === "remote";
	const canManage = !isRemote && (Boolean(onRename) || Boolean(onTogglePin) || Boolean(onToggleArchive) || Boolean(onDelete));

	return (
		<Paper
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
				background: selected ? "var(--mantine-primary-color-light)" : "transparent",
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
							onChange={onRenameDraftChange}
							onKeyDown={(event) => onRenameKeyDown(event, conversation.id)}
							onBlur={() => onCommitRename(conversation.id)}
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
												onClick={() => onBeginRename(conversation)}
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
												leftSection={conversation.isArchived ? <IconArchiveOff size={14} /> : <IconArchive size={14} />}
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
												label={t("pages.chat.conversationList.deleteShiftHint", "Shift-click to skip confirmation")}
												position="left"
												withArrow={true}
												openDelay={300}
											>
												<Menu.Item
													color="red"
													leftSection={<IconTrash size={14} />}
													onClick={(event: MouseEvent<HTMLButtonElement>) => onDelete(conversation.id, event.shiftKey)}
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
								label={t("pages.chat.conversationList.remoteTooltip", "Started from a paired client. View-only on this node.")}
								withArrow={true}
							>
								<Badge variant="light" color="blue" size="xs" data-testid={`conversation-remote-badge-${conversation.id}`}>
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
}
