import { ActionIcon, CopyButton, Group, Menu, Text, Tooltip } from "@mantine/core";
import {
	IconCheck,
	IconChevronLeft,
	IconChevronRight,
	IconCopy,
	IconDotsVertical,
	IconGauge,
	IconGitBranch,
	IconRefresh,
} from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { MessageFeedbackControl } from "@/features/chat/components/MessageFeedbackControl";
import type { ChatFeedbackRating, ChatMessageFeedback, ChatMessageModel } from "@/features/chat/models/ChatModels";
import { VoiceMessagePlayButton } from "@/features/voice/components/VoiceMessagePlayButton";

/** Prev/next navigation across the sibling revisions (variant group) of an assistant turn. */
export interface ChatMessageRevisionNav {
	activeIndex: number;
	total: number;
	onPrevious: () => void;
	onNext: () => void;
}

/** Which action slots are active for a given chat turn. Grouped to avoid a sprawl of individual boolean props. */
export interface ChatMessageActionCapabilities {
	copy: boolean;
	regenerate: boolean;
	branch: boolean;
	revisionNav: boolean;
	feedback: boolean;
	menu: boolean;
	showTokensPerSecond: boolean;
}

interface ChatMessageActionsProps {
	message: ChatMessageModel;
	capabilities: ChatMessageActionCapabilities;
	revisionNav?: ChatMessageRevisionNav;
	onRegenerate?: (messageId: string) => void;
	onBranch?: (messageId: string) => void;
	feedback?: ChatMessageFeedback;
	feedbackPending?: boolean;
	onSubmitFeedback?: (messageId: string, rating: ChatFeedbackRating, comment: string | undefined) => void;
	onToggleTokensPerSecond: () => void;
}

/**
 * The trailing action row of a chat turn: revision nav, copy, regenerate, branch, feedback, and the ⋮ options menu.
 * Extracted from ChatMessage to keep the parent readable; the gating booleans are computed in the parent and passed
 * down as a single `capabilities` object so this stays a pure presentational row.
 */
export function ChatMessageActions({
	message,
	capabilities,
	revisionNav,
	onRegenerate,
	onBranch,
	feedback,
	feedbackPending = false,
	onSubmitFeedback,
	onToggleTokensPerSecond,
}: ChatMessageActionsProps) {
	const {
		copy: canCopy,
		regenerate: canRegenerate,
		branch: canBranch,
		revisionNav: showRevisionNav,
		feedback: showFeedback,
		menu: showMenu,
		showTokensPerSecond,
	} = capabilities;
	const { t } = useTranslation();

	return (
		<Group gap={2} align="center" data-testid={`chat-message-actions-${message.id}`}>
			{showRevisionNav && revisionNav ? (
				<Group gap={0} align="center" data-testid={`message-revision-nav-${message.id}`}>
					<Tooltip label={t("pages.chat.revisions.previous", "Previous revision")} withArrow={true}>
						<ActionIcon
							aria-label={t("pages.chat.revisions.previous", "Previous revision")}
							color="gray"
							variant="subtle"
							size="sm"
							disabled={revisionNav.activeIndex <= 0}
							onClick={revisionNav.onPrevious}
							data-testid={`message-revision-prev-${message.id}`}
						>
							<IconChevronLeft size={14} />
						</ActionIcon>
					</Tooltip>
					<Text size="xs" c="dimmed" data-testid={`message-revision-count-${message.id}`}>
						{revisionNav.activeIndex + 1}/{revisionNav.total}
					</Text>
					<Tooltip label={t("pages.chat.revisions.next", "Next revision")} withArrow={true}>
						<ActionIcon
							aria-label={t("pages.chat.revisions.next", "Next revision")}
							color="gray"
							variant="subtle"
							size="sm"
							disabled={revisionNav.activeIndex >= revisionNav.total - 1}
							onClick={revisionNav.onNext}
							data-testid={`message-revision-next-${message.id}`}
						>
							<IconChevronRight size={14} />
						</ActionIcon>
					</Tooltip>
				</Group>
			) : null}
			{canCopy ? (
				<CopyButton value={message.content} timeout={2000}>
					{({ copied, copy }) => (
						<Tooltip
							label={
								copied
									? t("pages.chat.actions.copySuccess", "Message copied to clipboard.")
									: t("pages.chat.actions.copy", "Copy message")
							}
							withArrow={true}
						>
							<ActionIcon
								aria-label={t("pages.chat.actions.copy", "Copy message")}
								color={copied ? "teal" : "gray"}
								variant="subtle"
								size="sm"
								onClick={copy}
							>
								{copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
							</ActionIcon>
						</Tooltip>
					)}
				</CopyButton>
			) : null}
			<VoiceMessagePlayButton message={message} />
			{canRegenerate ? (
				<Tooltip label={t("pages.chat.actions.regenerate", "Regenerate response")} withArrow={true}>
					<ActionIcon
						aria-label={t("pages.chat.actions.regenerate", "Regenerate response")}
						color="gray"
						variant="subtle"
						size="sm"
						onClick={() => onRegenerate?.(message.id)}
					>
						<IconRefresh size={14} />
					</ActionIcon>
				</Tooltip>
			) : null}
			{canBranch ? (
				<Tooltip label={t("pages.chat.actions.branch", "Branch from here")} withArrow={true}>
					<ActionIcon
						aria-label={t("pages.chat.actions.branch", "Branch from here")}
						color="gray"
						variant="subtle"
						size="sm"
						onClick={() => onBranch?.(message.id)}
						data-testid={`message-branch-${message.id}`}
					>
						<IconGitBranch size={14} />
					</ActionIcon>
				</Tooltip>
			) : null}
			{showFeedback && onSubmitFeedback ? (
				<MessageFeedbackControl
					messageId={message.id}
					feedback={feedback}
					pending={feedbackPending}
					onSubmit={(rating, comment) => onSubmitFeedback(message.id, rating, comment)}
				/>
			) : null}
			{showMenu ? (
				<Menu position="bottom-end" withinPortal={true}>
					<Menu.Target>
						<ActionIcon
							aria-label={t("pages.chat.menu.label", "Message options")}
							color="gray"
							variant="subtle"
							size="sm"
							data-testid={`chat-message-menu-${message.id}`}
						>
							<IconDotsVertical size={14} />
						</ActionIcon>
					</Menu.Target>
					<Menu.Dropdown>
						<Menu.Label>{t("pages.chat.menu.label", "Message options")}</Menu.Label>
						<Menu.Item
							leftSection={<IconGauge size={14} />}
							rightSection={showTokensPerSecond ? <IconCheck size={14} /> : null}
							closeMenuOnClick={false}
							onClick={onToggleTokensPerSecond}
							data-testid={`chat-message-menu-tps-${message.id}`}
						>
							{t("pages.chat.menu.showTokensPerSecond", "Show tokens/sec")}
						</Menu.Item>
					</Menu.Dropdown>
				</Menu>
			) : null}
		</Group>
	);
}
