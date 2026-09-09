import type { RefObject } from "react";
import { useEffect, useRef } from "react";

// A scroll landing within this many pixels of the bottom counts as "at the bottom" and keeps auto-scroll
// latched on; scrolling further up unlatches it so a user reading earlier output mid-generation is left alone.
const NEAR_BOTTOM_THRESHOLD_PX = 100;

interface StickToBottomScrollInput {
	// The ScrollArea viewport: a scroll listener on it drives the stick-to-bottom latch. Owned by the caller
	// because the row virtualizer needs it before this hook runs.
	viewportRef: RefObject<HTMLDivElement | null>;
	endRef: RefObject<HTMLDivElement | null>;
	// The virtualized path's measured total row height; 0 whenever the plain path renders.
	virtualTotalSize: number;
	// Changes whenever rendered content grows (new revision group, tool entry, or streamed character).
	scrollKey: string;
	isStreamingActive: boolean;
	conversationId?: string;
	streamingTurnId?: string;
}

/**
 * Follows new content to the bottom of the message list while the reader has not scrolled away. Registers no
 * state, so the caller re-renders only for its own reasons; the latch lives in a ref.
 */
export function useStickToBottomScroll({
	viewportRef,
	endRef,
	virtualTotalSize,
	scrollKey,
	isStreamingActive,
	conversationId,
	streamingTurnId,
}: StickToBottomScrollInput): void {
	// Whether auto-scroll should follow new content. Latched from actual scroll position (scroll listener), NOT
	// inferred from geometry after content grows — a single large coalesced frame can add >100px in one commit,
	// so measuring distance post-growth would wrongly disengage and strand the stream off-screen. The user
	// scrolling up unlatches; scrolling back near the bottom re-latches. Defaults on so a fresh list sticks.
	const stickToBottomRef = useRef(true);

	// Virtualized path only: row heights land asynchronously (estimate → measured), growing the total size after
	// the scrollKey-driven follow already ran. While latched, re-pin on every total-size change so the view stays
	// at the bottom as measurements (and the streaming row's growth) arrive. "auto" — this fires per measurement,
	// stacking smooth animations would judder. Declared FIRST so that in a commit where it and the scrollKey
	// follow below both fire, the follow's "smooth" lands last and wins, exactly as it did before this hook.
	useEffect(() => {
		if (virtualTotalSize > 0 && stickToBottomRef.current) {
			endRef.current?.scrollIntoView({ behavior: "auto", block: "end" });
		}
	}, [virtualTotalSize, endRef]);

	// Drive the stick-to-bottom latch from real scroll events: unlatch once the user scrolls further than the
	// threshold from the bottom, re-latch when they return near it. Our own scrollIntoView also lands near the
	// bottom, so it keeps the latch engaged. Attached once; the viewport ref is populated by commit time.
	useEffect(() => {
		const viewport = viewportRef.current;
		if (!viewport) {
			return;
		}

		const handleScroll = (): void => {
			const distanceFromBottom = viewport.scrollHeight - viewport.scrollTop - viewport.clientHeight;
			stickToBottomRef.current = distanceFromBottom <= NEAR_BOTTOM_THRESHOLD_PX;
		};

		viewport.addEventListener("scroll", handleScroll, { passive: true });
		return () => viewport.removeEventListener("scroll", handleScroll);
	}, [viewportRef]);

	// Re-latch when the thread changes or a NEW streaming turn begins so switching conversations or sending a
	// message returns the view to the newest content — but never when a turn CLEARS on completion, which must
	// leave a scrolled-up reader exactly where they are (that terminal case is guarded by the latch below).
	const previousStreamingTurnRef = useRef<string | undefined>(undefined);
	const previousConversationIdRef = useRef<string | undefined>(undefined);
	useEffect(() => {
		const turnStarted = Boolean(streamingTurnId) && streamingTurnId !== previousStreamingTurnRef.current;
		const conversationChanged = conversationId !== previousConversationIdRef.current;
		if (turnStarted || conversationChanged) {
			stickToBottomRef.current = true;
		}
		previousStreamingTurnRef.current = streamingTurnId;
		previousConversationIdRef.current = conversationId;
	}, [conversationId, streamingTurnId]);

	useEffect(() => {
		// scrollKey is the re-run trigger: it changes whenever rendered content grows (new revision group, tool
		// entry, or streamed character), which is exactly when we may need to follow the stream. Reading it here
		// also keeps it a declared dependency.
		if (scrollKey.length === 0) {
			return;
		}

		// Only follow the stream while latched (fixes both the large-frame disengage and the terminal-completion
		// yank). Jump with "auto" during streaming so per-frame growth doesn't stack overlapping smooth-scroll
		// animations; use "smooth" for one-off transitions (open/switch conversation, turn completion).
		if (!stickToBottomRef.current) {
			return;
		}

		endRef.current?.scrollIntoView({ behavior: isStreamingActive ? "auto" : "smooth", block: "end" });
	}, [scrollKey, isStreamingActive, endRef]);
}
