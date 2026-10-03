import { Button, Tooltip } from "@mantine/core";
import { IconPlayerTrackNext } from "@tabler/icons-react";
import { useMutation } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";

import { answerNowNodeChatMessageMutation } from "@/core/api/generated/@tanstack/react-query.gen";
import { toast } from "@/core/ui/notifications/Toast";

interface AnswerNowButtonProps {
	messageId: string;
}

/**
 * Ends the model's reasoning so it answers with what it has, without stopping the message. Shown beside Stop only
 * while a reasoning segment streams on a model that supports it; a refusal (409: no longer thinking, or the model
 * could not be asked) is a toast, never a failed turn.
 */
export function AnswerNowButton({ messageId }: AnswerNowButtonProps) {
	const { t } = useTranslation();
	const answerNow = useMutation({
		...answerNowNodeChatMessageMutation(),
		onError: () => {
			toast.warn(t("pages.chat.answerNowUnavailable", "The model can't be asked to answer early right now."));
		},
	});

	return (
		<Tooltip label={t("pages.chat.answerNowHint", "Stop thinking and answer with what the model has so far")}>
			<Button
				data-testid="chat-answer-now-button"
				variant="light"
				size="sm"
				style={{ flexShrink: 0 }}
				leftSection={<IconPlayerTrackNext size={13} />}
				loading={answerNow.isPending}
				onClick={() => answerNow.mutate({ path: { messageId } })}
			>
				{t("pages.chat.answerNow", "Answer now")}
			</Button>
		</Tooltip>
	);
}
