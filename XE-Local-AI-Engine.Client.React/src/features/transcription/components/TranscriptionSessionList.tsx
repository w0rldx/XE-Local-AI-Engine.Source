import { ActionIcon, Badge, Card, Group, Stack, Text, UnstyledButton } from "@mantine/core";
import { IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import type { TranscriptionSessionView } from "@/features/transcription/models/TranscriptionModels";

interface TranscriptionSessionListProps {
	readonly sessions: readonly TranscriptionSessionView[];
	readonly deletingSessionId: string | null;
	readonly onOpen: (sessionId: string) => void;
	readonly onDelete: (session: TranscriptionSessionView) => void;
}

// Status colours: a finished transcript is green, a refused or failed one red, anything still moving neutral.
const statusColors: Record<TranscriptionSessionView["status"], string> = {
	Created: "gray",
	Transcribing: "blue",
	Completed: "green",
	Failed: "red",
	Cancelled: "gray",
};

/** The session history: one card per row, newest first as the endpoint returns them. */
export function TranscriptionSessionList({ sessions, deletingSessionId, onOpen, onDelete }: TranscriptionSessionListProps) {
	const { t } = useTranslation();

	return (
		<Stack gap="sm" data-testid="transcription-session-list">
			{sessions.map((session) => {
				const title = session.title ?? t("pages.transcription.list.untitled");
				return (
					// The card stays a plain container: it holds the delete icon, and an ARIA `button` around it makes
					// children presentational, which would hide the very control it wraps. The title below is a real
					// button instead — the card's onClick is only the pointer affordance.
					<Card
						key={session.id}
						withBorder={true}
						padding="sm"
						onClick={() => onOpen(session.id)}
						style={{ cursor: "pointer" }}
						data-testid={`transcription-session-card-${session.id}`}
					>
						<Group justify="space-between" wrap="nowrap" align="flex-start">
							<Stack gap={4} style={{ minWidth: 0 }}>
								{/* The control a keyboard and a screen reader reach, named by its own text. A click here also
								    bubbles to the card, so it stops propagating rather than opening the session twice. */}
								<UnstyledButton
									onClick={(event) => {
										event.stopPropagation();
										onOpen(session.id);
									}}
									style={{ minWidth: 0, textAlign: "inherit" }}
									data-testid={`transcription-session-open-${session.id}`}
								>
									<Text fw={500} truncate="end">
										{title}
									</Text>
								</UnstyledButton>
								<Text size="xs" c="dimmed">
									{t("pages.transcription.list.meta", {
										count: session.segmentCount,
										created: formatTimestamp(session.createdAtUtc),
									})}
								</Text>
							</Stack>
							<Group gap="xs" wrap="nowrap">
								<Badge
									color={statusColors[session.status]}
									variant="light"
									data-testid={`transcription-session-status-${session.id}`}
								>
									{t(`pages.transcription.status.${session.status}`)}
								</Badge>
								<ActionIcon
									variant="subtle"
									color="red"
									aria-label={t("pages.transcription.list.delete")}
									loading={deletingSessionId === session.id}
									onClick={(event) => {
										// The card itself navigates; deleting must not also open the row it just removed.
										event.stopPropagation();
										onDelete(session);
									}}
									data-testid={`transcription-session-delete-${session.id}`}
								>
									<IconTrash size={16} />
								</ActionIcon>
							</Group>
						</Group>
					</Card>
				);
			})}
		</Stack>
	);
}
