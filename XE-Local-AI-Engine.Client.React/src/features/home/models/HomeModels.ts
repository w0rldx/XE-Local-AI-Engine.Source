import type { XeLocalAiEngineClientEndpointsLocalChatV1NodeChatConversationSummaryResponse as ConversationSummaryDto } from "@/core/api/generated";
import type { GgufAcquisitionStatus } from "@/core/model-downloads/models/GgufAcquisitionModels";

/** One row of Home's recent-chats card: Home's own view of a conversation summary, not chat's model. */
export interface RecentConversation {
	readonly id: string;
	// Empty when the conversation has no stored title; the card supplies the label.
	readonly title: string;
	// Epoch millis of the last activity.
	readonly lastSeenUtc: number;
	readonly preview: string;
}

export type GreetingPeriod = "morning" | "afternoon" | "evening";

// The list endpoint answers pinned threads first, then by last activity. Home shows activity order, so it asks for
// a wider page than it renders and re-sorts. A node with more pinned threads than this page holds can miss a recent
// unpinned one; a server-side sort option would close that if it ever matters.
export const RECENT_CONVERSATION_FETCH_LIMIT = 25;
const RECENT_CONVERSATION_COUNT = 5;

export function toRecentConversations(items: readonly ConversationSummaryDto[]): RecentConversation[] {
	return items
		.filter((item) => !item.purged && !item.archived)
		.map((item) => ({
			id: item.conversationId,
			title: item.title?.trim() ?? "",
			lastSeenUtc: item.lastSeenUtc,
			preview: item.lastMessagePreview?.trim() ?? "",
		}))
		.sort((a, b) => b.lastSeenUtc - a.lastSeenUtc)
		.slice(0, RECENT_CONVERSATION_COUNT);
}

// From the user's local clock: before noon is morning, before 18:00 afternoon, the rest evening.
export function greetingPeriod(hour: number): GreetingPeriod {
	if (hour < 12) {
		return "morning";
	}
	return hour < 18 ? "afternoon" : "evening";
}

// Progress of the first-run download in percent, only from what the server reported: its own percentage, else the
// byte counts. Undefined means indeterminate; a guess would be worse than an honest spinner.
export function setupDownloadPercent(download: GgufAcquisitionStatus): number | undefined {
	if (download.pct !== undefined) {
		return download.pct;
	}
	if (download.totalBytes && download.completedBytes !== null && download.completedBytes !== undefined) {
		return Math.min(100, Math.round((download.completedBytes / download.totalBytes) * 100));
	}
	return undefined;
}
