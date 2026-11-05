import { NavLink, Skeleton, Stack, Text } from "@mantine/core";
import { IconMessageCircle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { useChatHandOff } from "@/features/home/hooks/useChatHandOff";
import { useRecentConversations } from "@/features/home/queries/useRecentConversations";

const lastSeenFormat: Intl.DateTimeFormatOptions = { dateStyle: "medium", timeStyle: "short" };

// The last few conversations by activity; a row opens that conversation in chat.
export function HomeRecentChatsCard() {
	const { t } = useTranslation();
	const { openConversation } = useChatHandOff();
	const recent = useRecentConversations();

	return (
		<SectionCard
			title={t("pages.home.recent.title", "Recent chats")}
			icon={<IconMessageCircle size={22} />}
			gap="xs"
			data-testid="home-recent"
		>
			{recent.isLoading ? (
				<Stack gap="xs" data-testid="home-recent-loading">
					<Skeleton height={36} />
					<Skeleton height={36} />
				</Stack>
			) : recent.isError ? (
				<InlineErrorAlert
					message={apiErrorMessage(recent.error, t("pages.home.recent.loadError", "Recent chats could not be loaded."))}
					data-testid="home-recent-error"
				/>
			) : (recent.data ?? []).length === 0 ? (
				<Text c="dimmed" size="sm" data-testid="home-recent-empty">
					{t("pages.home.recent.empty", "No chats yet. Your conversations show up here.")}
				</Text>
			) : (
				<Stack gap={2}>
					{(recent.data ?? []).map((conversation) => (
						<NavLink
							key={conversation.id}
							component="button"
							type="button"
							onClick={() => openConversation(conversation.id)}
							label={conversation.title || t("pages.home.recent.untitled", "Untitled chat")}
							description={
								conversation.preview.length > 0
									? `${formatTimestamp(conversation.lastSeenUtc, lastSeenFormat)} · ${conversation.preview}`
									: formatTimestamp(conversation.lastSeenUtc, lastSeenFormat)
							}
							// One line per row: a long preview truncates instead of growing the card.
							styles={{
								body: { minWidth: 0 },
								description: { overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" },
							}}
							data-testid="home-recent-item"
						/>
					))}
				</Stack>
			)}
		</SectionCard>
	);
}
