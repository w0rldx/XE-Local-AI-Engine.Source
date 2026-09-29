import { ActionIcon, Button, Stack, Text, Tooltip } from "@mantine/core";
import { IconWorldCheck } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import {
	getNodeSettingsOptions,
	getTutorialStateOptions,
	getTutorialStateQueryKey,
	saveTutorialStateMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { useSetWebContentAutoAccept, useWebContentAutoAccept } from "@/features/chat/stores/WebContentAutoAcceptStore";

// The per-user acknowledgement of the auto-accept risk notice (ADR 0017, D8). The wire key is what the server stores,
// so renaming it re-shows the notice to everyone.
const autoAcceptNoticeKey = "web-content-auto-accept-notice";

interface WebContentAutoAcceptToggleProps {
	conversationId: string;
	disabled: boolean;
}

/**
 * The composer's per-conversation "Auto-accept web content" switch. Rendered only when the node allows web access; the
 * caller shows it only while local tools are on. A conversation without an id yet (a draft before its first send) keeps
 * the switch disabled: every conversation starts in review mode, so nothing is lost by choosing after the first turn.
 */
export function WebContentAutoAcceptToggle({ conversationId, disabled }: WebContentAutoAcceptToggleProps) {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const settings = useQuery(withResponseValidation(getNodeSettingsOptions()));
	const webAccessEnabled = settings.data?.webAccessEnabled === true;
	const tutorialState = useQuery({ ...withResponseValidation(getTutorialStateOptions()), enabled: webAccessEnabled });
	const saveTutorialState = useMutation(withResponseValidation(saveTutorialStateMutation()));
	const autoAccept = useWebContentAutoAccept(conversationId);
	const setAutoAccept = useSetWebContentAutoAccept();
	const [noticeOpen, setNoticeOpen] = useState(false);
	const [acknowledgedHere, setAcknowledgedHere] = useState(false);

	if (!webAccessEnabled) {
		return null;
	}

	const acknowledged =
		acknowledgedHere ||
		tutorialState.data?.entries.some((entry) => entry.key === autoAcceptNoticeKey && entry.status === "completed") === true;
	const hasConversation = conversationId.length > 0;
	const label = t("pages.chat.webAutoAccept.label", "Auto-accept web content");

	const handleClick = () => {
		if (autoAccept) {
			setAutoAccept(conversationId, false);
		} else if (acknowledged) {
			setAutoAccept(conversationId, true);
		} else {
			setNoticeOpen(true);
		}
	};

	const handleConfirm = () => {
		setNoticeOpen(false);
		// Enabled for this conversation whatever the save does; a failed save only means the notice shows again next time.
		setAutoAccept(conversationId, true);
		saveTutorialState.mutate(
			{ body: { key: autoAcceptNoticeKey, status: "completed" } },
			{
				onSuccess: () => {
					setAcknowledgedHere(true);
					queryClient.invalidateQueries({ queryKey: getTutorialStateQueryKey() }).catch(() => undefined);
				},
			},
		);
	};

	return (
		<>
			<Tooltip
				label={
					!hasConversation
						? t("pages.chat.webAutoAccept.needsConversation", "Send a first message to choose how web content is handled")
						: autoAccept
							? t("pages.chat.webAutoAccept.on", "Web content reaches the model without review")
							: t("pages.chat.webAutoAccept.off", "Web content is shown to you for review first")
				}
			>
				<ActionIcon
					size={36}
					variant={autoAccept ? "light" : "subtle"}
					color={autoAccept ? "orange" : "gray"}
					// Held while the acknowledgement loads, so a click never re-asks a user who already answered.
					disabled={disabled || !hasConversation || tutorialState.isPending}
					onClick={handleClick}
					aria-label={label}
					aria-pressed={autoAccept}
					data-testid="chat-web-auto-accept-toggle"
				>
					<IconWorldCheck size={15} />
				</ActionIcon>
			</Tooltip>
			<DialogShell
				opened={noticeOpen}
				onClose={() => setNoticeOpen(false)}
				title={t("pages.chat.webAutoAccept.noticeTitle", "Auto-accept web content?")}
				size="min(32rem, 95vw)"
				enableFullScreenToggle={false}
				data-testid="chat-web-auto-accept-notice"
				footer={
					<>
						<Button variant="subtle" onClick={() => setNoticeOpen(false)} data-testid="chat-web-auto-accept-cancel">
							{t("common.cancel", "Cancel")}
						</Button>
						<Button color="orange" onClick={handleConfirm} data-testid="chat-web-auto-accept-confirm">
							{t("pages.chat.webAutoAccept.confirm", "Enable auto-accept")}
						</Button>
					</>
				}
			>
				<Stack gap="sm">
					<Text size="sm">
						{t(
							"pages.chat.webAutoAccept.noticeInjection",
							"Web pages and search results can contain hidden instructions (prompt injection) written to make the model leak your data or take actions you did not ask for.",
						)}
					</Text>
					<Text size="sm">
						{t(
							"pages.chat.webAutoAccept.noticeUnreviewed",
							"With auto-accept on, the model's web requests in this conversation are sent without asking you, and their results reach the model without being shown to you first.",
						)}
					</Text>
					<Text size="sm">{t("pages.chat.webAutoAccept.noticeReversible", "You can turn it off again at any time.")}</Text>
				</Stack>
			</DialogShell>
		</>
	);
}
