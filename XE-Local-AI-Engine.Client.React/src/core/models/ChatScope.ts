/**
 * Embeds the chat page inside a feature that OWNS a conversation (today: a work session). Everything the chat page
 * does — the readiness gate, the streaming fold, the tool timeline, the cold-load re-attach — stays in `Chat`; this
 * prop only pins the view and redirects the composer. `/chat` passes nothing and runs unpinned.
 */
export interface ChatScope {
	/** Pin the view to exactly this conversation: no sidebar, no selection writes to the global preference store. */
	readonly conversationId: string;
	/** Owner-pinned agent. Forces agent mode on and renders the agent + model selectors read-only. */
	readonly pinnedAgentId?: string;
	/** Bumped by the owner when a NEW server-side turn starts on the same conversation; re-arms the re-attach. */
	readonly resumeNonce?: number;
	/**
	 * Composer target. When set, the composer posts here instead of starting a chat invocation — the owner's
	 * supervisor stays the single writer of invocations on this conversation. A REJECTED promise keeps the draft.
	 */
	readonly onSendOverride?: (content: string) => Promise<void>;
	/** Stop-button target (a work session maps it to pause). */
	readonly onStopOverride?: () => void;
	/** Disables the composer (a terminal session takes no further input). */
	readonly composerDisabled?: boolean;
	/** The parent owns the full-height frame, so `Chat` renders bare. */
	readonly embedded?: boolean;
}
