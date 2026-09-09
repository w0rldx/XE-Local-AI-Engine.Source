import { Group, Paper, Stack, Text } from "@mantine/core";
import type { ReactNode } from "react";

import { ChatMarkdown } from "@/features/chat/components/ChatMarkdown";
import type { ChatMessageDisplay } from "@/features/chat/models/ChatMessageDisplay";
import type { ChatMessageModel } from "@/features/chat/models/ChatModels";

interface UserMessageBubbleProps {
	message: ChatMessageModel;
	display: ChatMessageDisplay;
	actions: ReactNode;
	footer?: ReactNode;
}

export function UserMessageBubble({ message, display, actions, footer }: UserMessageBubbleProps) {
	return (
		<Group justify="flex-end" align="flex-end" wrap="nowrap" style={{ minWidth: 0 }} data-testid={`chat-message-${message.id}`}>
			<Stack gap={4} align="flex-end" style={{ maxWidth: "82%", minWidth: 0 }}>
				<Paper
					p="sm"
					style={{ background: "var(--mantine-primary-color-light)", borderRadius: "14px 14px 4px 14px", minWidth: 0 }}
				>
					{display.content ? <ChatMarkdown content={display.content} /> : null}
				</Paper>
				<Group gap={4} align="center">
					<span data-testid={`chat-message-role-${message.id}`} style={{ position: "absolute", left: "-10000px" }}>
						{display.label}
					</span>
					{display.time ? (
						<Text size="xs" c="dimmed">
							{display.time}
						</Text>
					) : null}
					{actions}
				</Group>
				{footer}
			</Stack>
		</Group>
	);
}
