import type { TFunction } from "i18next";

// The persisted placeholder title; it stays English on the wire because turn resolution compares it by exact string.
export const placeholderConversationTitle = "New conversation";

// What the chat mapper stores for a conversation the server returns without a title.
export const untitledConversationTitle = "Untitled conversation";

/**
 * The trimmed title to display; only the placeholder and untitled fallback titles are localized. Empty for a blank
 * title, so each site keeps its own fallback.
 */
export function conversationDisplayTitle(title: string | null | undefined, t: TFunction): string {
	const trimmed = title?.trim() ?? "";
	if (trimmed === placeholderConversationTitle) {
		return t("pages.chat.conversationList.placeholderTitle", "New conversation");
	}
	return trimmed === untitledConversationTitle ? t("pages.chat.conversationList.untitled", "Untitled conversation") : trimmed;
}
