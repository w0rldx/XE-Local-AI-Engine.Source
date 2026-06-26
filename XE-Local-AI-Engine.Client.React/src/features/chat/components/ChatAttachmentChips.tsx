import { ActionIcon, Badge, Group, Loader, Paper, Text, Tooltip } from "@mantine/core";
import { IconFileText, IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { formatAttachmentSize } from "@/features/chat/models/ChatAttachmentModels";
import type { ChatAttachment, ChatAttachmentStatus, PendingAttachmentUpload } from "@/features/chat/models/ChatAttachmentModels";

interface ChatAttachmentChipsProps {
	attachments: ChatAttachment[];
	pendingUploads: PendingAttachmentUpload[];
	onRemove: (fileId: string) => void;
	disabled?: boolean;
}

const statusColors: Record<ChatAttachmentStatus, string> = {
	extracted: "teal",
	pending: "gray",
	unsupported: "yellow",
	failed: "red",
	unknown: "gray",
};

function statusLabel(status: ChatAttachmentStatus, t: (key: string, fallback: string) => string): string {
	switch (status) {
		case "extracted":
			return t("pages.chat.composer.attachments.status.extracted", "Extracted");
		case "pending":
			return t("pages.chat.composer.attachments.status.pending", "Pending");
		case "unsupported":
			return t("pages.chat.composer.attachments.status.unsupported", "Unsupported");
		case "failed":
			return t("pages.chat.composer.attachments.status.failed", "Failed");
		default:
			return t("pages.chat.composer.attachments.status.unknown", "Unknown");
	}
}

// Renders the attached-files chip row shown above the composer input: one chip per uploaded file (name, size,
// extraction status, remove) plus an optimistic "uploading" chip per in-flight upload. Renders nothing when there
// is nothing to show, so the composer keeps its compact height until a file is attached.
export function ChatAttachmentChips({ attachments, pendingUploads, onRemove, disabled = false }: ChatAttachmentChipsProps) {
	const { t } = useTranslation();

	if (attachments.length === 0 && pendingUploads.length === 0) {
		return null;
	}

	return (
		<Group gap="xs" mb="xs" wrap="wrap" data-testid="chat-attachment-chips">
			{pendingUploads.map((upload) => (
				<Paper key={upload.tempId} withBorder={true} radius="sm" px="xs" py={4} data-testid="chat-attachment-pending">
					<Group gap={6} wrap="nowrap">
						<Loader size={14} />
						<Text size="xs" lineClamp={1} style={{ maxWidth: 160 }}>
							{upload.name}
						</Text>
						<Text size="xs" c="dimmed">
							{Math.round(upload.percent)}%
						</Text>
					</Group>
				</Paper>
			))}
			{attachments.map((attachment) => (
				<Paper key={attachment.fileId} withBorder={true} radius="sm" px="xs" py={4} data-testid="chat-attachment-chip">
					<Group gap={6} wrap="nowrap">
						<IconFileText size={14} />
						<Tooltip label={attachment.originalFileName} withArrow={true}>
							<Text size="xs" lineClamp={1} style={{ maxWidth: 160 }}>
								{attachment.originalFileName}
							</Text>
						</Tooltip>
						<Text size="xs" c="dimmed">
							{formatAttachmentSize(attachment.sizeBytes)}
						</Text>
						<Badge size="xs" variant="light" color={statusColors[attachment.status]} data-testid="chat-attachment-status">
							{statusLabel(attachment.status, t)}
						</Badge>
						<ActionIcon
							size="xs"
							variant="subtle"
							color="gray"
							disabled={disabled}
							onClick={() => onRemove(attachment.fileId)}
							aria-label={t("pages.chat.composer.attachments.remove", "Remove {{name}}", { name: attachment.originalFileName })}
							data-testid="chat-attachment-remove"
						>
							<IconX size={12} />
						</ActionIcon>
					</Group>
				</Paper>
			))}
		</Group>
	);
}
