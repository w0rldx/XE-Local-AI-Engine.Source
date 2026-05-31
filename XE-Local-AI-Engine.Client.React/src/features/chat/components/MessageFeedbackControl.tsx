import { ActionIcon, Button, Group, Popover, Stack, Text, Textarea, Tooltip } from "@mantine/core";
import { IconThumbDown, IconThumbDownFilled, IconThumbUp, IconThumbUpFilled } from "@tabler/icons-react";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import type { ChatFeedbackRating, ChatMessageFeedback } from "@/features/chat/models/ChatModels";

/* eslint-disable react-doctor/no-derived-state, react-doctor/no-event-handler */

interface MessageFeedbackControlProps {
	messageId: string;
	feedback?: ChatMessageFeedback;
	pending?: boolean;
	onSubmit: (rating: ChatFeedbackRating, comment: string | undefined) => void;
}

/**
 * Node-local feedback affordance: thumbs up/down with an optional comment captured in a popover.
 * Clicking a thumb opens the comment popover pre-seeded with that rating; submitting upserts via PUT .../feedback.
 */
export function MessageFeedbackControl({ messageId, feedback, pending = false, onSubmit }: MessageFeedbackControlProps) {
	const { t } = useTranslation();
	const [opened, setOpened] = useState(false);
	const [draftRating, setDraftRating] = useState<ChatFeedbackRating>(feedback?.rating ?? "up");
	const [comment, setComment] = useState(feedback?.comment ?? "");

	useEffect(() => {
		// Keep the draft in sync when the persisted feedback changes (e.g. after a refetch).
		setComment(feedback?.comment ?? "");
		if (feedback?.rating) {
			setDraftRating(feedback.rating);
		}
	}, [feedback?.comment, feedback?.rating]);

	const openWith = (rating: ChatFeedbackRating): void => {
		setDraftRating(rating);
		setComment(feedback?.comment ?? "");
		setOpened(true);
	};

	const submit = (): void => {
		const trimmed = comment.trim();
		onSubmit(draftRating, trimmed.length > 0 ? trimmed : undefined);
		setOpened(false);
	};

	const upActive = feedback?.rating === "up";
	const downActive = feedback?.rating === "down";

	return (
		<Popover
			opened={opened}
			onChange={setOpened}
			position="top"
			withArrow={true}
			trapFocus={true}
			width={280}
			data-testid={`message-feedback-${messageId}`}
		>
			<Popover.Target>
				<Group gap={2} align="center">
					<Tooltip label={t("pages.chat.feedback.up", "Good response")} withArrow={true}>
						<ActionIcon
							aria-label={t("pages.chat.feedback.up", "Good response")}
							color={upActive ? "teal" : "gray"}
							variant="subtle"
							size="sm"
							loading={pending}
							onClick={() => openWith("up")}
							data-testid={`message-feedback-up-${messageId}`}
						>
							{upActive ? <IconThumbUpFilled size={14} /> : <IconThumbUp size={14} />}
						</ActionIcon>
					</Tooltip>
					<Tooltip label={t("pages.chat.feedback.down", "Bad response")} withArrow={true}>
						<ActionIcon
							aria-label={t("pages.chat.feedback.down", "Bad response")}
							color={downActive ? "red" : "gray"}
							variant="subtle"
							size="sm"
							loading={pending}
							onClick={() => openWith("down")}
							data-testid={`message-feedback-down-${messageId}`}
						>
							{downActive ? <IconThumbDownFilled size={14} /> : <IconThumbDown size={14} />}
						</ActionIcon>
					</Tooltip>
				</Group>
			</Popover.Target>
			<Popover.Dropdown>
				<Stack gap={8}>
					<Text size="sm" fw={600}>
						{draftRating === "up"
							? t("pages.chat.feedback.commentTitleUp", "What was helpful?")
							: t("pages.chat.feedback.commentTitleDown", "What went wrong?")}
					</Text>
					<Textarea
						value={comment}
						onChange={(event) => setComment(event.currentTarget.value)}
						placeholder={t("pages.chat.feedback.commentPlaceholder", "Add a comment (optional)")}
						autosize={true}
						minRows={2}
						maxRows={5}
						data-testid={`message-feedback-comment-${messageId}`}
						aria-label={t("pages.chat.feedback.commentAria", "Feedback comment")}
					/>
					<Group justify="flex-end" gap={8}>
						<Button variant="subtle" size="xs" color="gray" onClick={() => setOpened(false)}>
							{t("common.cancel", "Cancel")}
						</Button>
						<Button size="xs" loading={pending} onClick={submit} data-testid={`message-feedback-submit-${messageId}`}>
							{t("pages.chat.feedback.submit", "Submit")}
						</Button>
					</Group>
				</Stack>
			</Popover.Dropdown>
		</Popover>
	);
}
