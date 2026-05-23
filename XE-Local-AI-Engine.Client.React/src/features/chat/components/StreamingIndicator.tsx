import { Badge, Group, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

interface StreamingIndicatorProps {
	error?: string;
	failureCategory?: string;
	hasContent?: boolean;
	isDelayed?: boolean;
	isActive: boolean;
}

export function StreamingIndicator({ error, failureCategory, hasContent = false, isDelayed = false, isActive }: StreamingIndicatorProps) {
	const { t } = useTranslation();

	if (error) {
		return (
			<Group gap="xs">
				<Text size="sm" c="red" data-testid="chat-stream-error">
					{error}
				</Text>
				{failureCategory ? (
					<Badge color="red" size="sm" variant="light">
						{failureCategory}
					</Badge>
				) : null}
			</Group>
		);
	}

	if (!isActive || !hasContent) {
		return null;
	}

	return (
		<Text size="sm" c="dimmed" data-testid={isDelayed ? "chat-stream-delayed-indicator" : "chat-streaming-indicator"}>
			{isDelayed ? t("pages.chat.waitingForResponse", "Waiting for response") : t("pages.chat.streaming", "streaming")}
		</Text>
	);
}
