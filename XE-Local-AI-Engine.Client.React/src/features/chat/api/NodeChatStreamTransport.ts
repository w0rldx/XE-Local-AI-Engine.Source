import { nodeChatConnection } from "@/features/chat/api/NodeChatConnection";
import type { NodeChatStreamEventDto } from "@/features/chat/models/NodeChatStreamTypes";

/* eslint-disable react-doctor/async-await-in-loop -- The async-iterator bridge must await each SignalR event in wire order before reading the next one. */

// A terminal stream event ends the invocation; after one of these there is nothing left to resume.
const terminalStreamEventTypes = new Set<string>([
	"assistant-completed",
	"assistant-cancelled",
	"assistant-failed",
	"assistant-interrupted",
]);

function isTerminalStreamEvent(event: NodeChatStreamEventDto): boolean {
	return terminalStreamEventTypes.has(event.type);
}

// Wire-string literals for the delta-only protocol, mirroring nodeChatStreamEventTypes in NodeChatStreamState.ts.
// Duplicated here for the same reason NodeChatStreamGuard duplicates its own: the transport layer stays free of
// the stream-state module's import graph. Keep in sync with that source.
const assistantDeltaEventType = "assistant-delta";
// Replaces the client's accumulated text and re-bases the offset counters. Forwarded downstream.
const assistantSnapshotEventType = "assistant-snapshot";
// The server could not enqueue an event (bounded-queue overflow, oversized replay) and is asking the client to
// resynchronize. Consumed HERE and never forwarded — it is repaired exactly like an offset gap.
const assistantReconcileEventType = "assistant-reconcile";

/** The opening hub call for a stream: a local send, a server-driven regenerate, or a re-attach to a run. */
export interface NodeChatStreamOpening {
	method: "SendMessage" | "RegenerateMessage" | "ResumeMessage" | "ResumeConversation";
	args: unknown[];
	// The assistant message id the caller renders into. Known up front for a send; for a regenerate it is the
	// server-minted variant id, latched from the first event; for a resume it is the target id that resume
	// events (stamped with the invocation id) are remapped onto.
	assistantMessageId?: string;
	// The invocation/resume key (== requestId). Known up front for a send and a direct resume; for a regenerate
	// it is latched from the first event's requestId (server-minted run), enabling resume after a reconnect.
	knownInvocationId?: string;
	// True when the OPENING call is itself a ResumeMessage (re-attaching to a server-driven run). Then an
	// opening error means "unknown/terminal invocation" → complete cleanly, rather than surfacing a failure.
	isResumeOpening?: boolean;
}

/**
 * Streams a chat turn over the persistent connection and transparently resumes after a reconnect. When the
 * underlying connection drops mid-stream, the active SignalR subscription errors; rather than failing the
 * turn, we wait for the connection to reconnect with a new id and re-attach via the hub's `ResumeMessage`
 * (keyed by the invocation/request id). Resumed events carry the invocation id as their message id, so they
 * are remapped back to the assistant message id the caller is rendering. Used for both local sends and
 * server-driven regenerations (assistant revision flow) — the regenerate latches its server-minted ids from the first event.
 */
export function streamNodeChatEvents(opening: NodeChatStreamOpening, signal: AbortSignal): AsyncIterable<NodeChatStreamEventDto> {
	return {
		async *[Symbol.asyncIterator](): AsyncIterator<NodeChatStreamEventDto> {
			const values: NodeChatStreamEventDto[] = [];
			let completed = false;
			let failure: unknown;
			let wake: (() => void) | undefined;
			let reachedTerminal = false;
			let activeSubscription: { dispose: () => void } | undefined;
			// The request id doubles as the invocation id the resume registry keys on. Known up front for sends and
			// direct resumes; otherwise latched from the first event below.
			let invocationId = opening.knownInvocationId;
			let assistantMessageId = opening.assistantMessageId;

			const notify = (): void => {
				wake?.();
				wake = undefined;
			};

			// The highest sequence forwarded downstream. A resumed stream restarts its numbering at zero on the
			// server (the resume registry cannot see the original stream's counter), so its events are rebased
			// past this mark before they reach the sequence-deduping guard — otherwise the guard drops every
			// resumed event (terminal included) as a stale duplicate and the message sticks with no error.
			let lastPushedSequence = Number.NEGATIVE_INFINITY;

			// Where the next delta must begin. A delta frame carries only its delta plus the offset that delta
			// starts at, so a gap (a frame that never arrived) or an overlap (a replayed frame) is detectable
			// from the offsets alone, without the adapter holding any text. Undefined until the first frame
			// establishes a position; re-based by every snapshot/terminal, which carry the full text.
			let nextContentOffset: number | undefined;
			let nextReasoningOffset: number | undefined;

			const pushEvent = (event: NodeChatStreamEventDto): void => {
				// Latch ids from the first event when not known up front, so a reconnect can resume the run.
				invocationId ??= event.requestId || undefined;
				assistantMessageId ??= event.messageId || undefined;

				// A server-side reconcile request is an adapter instruction, not a message mutation: consume it
				// and re-enter through ResumeMessage. Nothing downstream ever sees this event type.
				if (event.type === assistantReconcileEventType) {
					repair();
					return;
				}

				if (event.type === assistantSnapshotEventType || isTerminalStreamEvent(event)) {
					nextContentOffset = event.content?.length ?? 0;
					nextReasoningOffset = event.reasoning?.length ?? 0;
				} else if (event.type === assistantDeltaEventType) {
					const contentOffset = event.contentOffset ?? 0;
					const reasoningOffset = event.reasoningOffset ?? 0;
					// A mismatch in either direction is unrecoverable by appending, so drop the frame and repair.
					// The frame's sequence is deliberately left unconsumed (lastPushedSequence is not advanced):
					// the resumed stream rebases onto it, so the sequence-ordering guard downstream never stalls
					// on the hole this drop would otherwise leave.
					if (
						(nextContentOffset !== undefined && contentOffset !== nextContentOffset) ||
						(nextReasoningOffset !== undefined && reasoningOffset !== nextReasoningOffset)
					) {
						repair();
						return;
					}
					nextContentOffset = contentOffset + (event.delta?.length ?? 0);
					nextReasoningOffset = reasoningOffset + (event.reasoningDelta?.length ?? 0);
				}

				if (isTerminalStreamEvent(event)) {
					reachedTerminal = true;
				}
				lastPushedSequence = Math.max(lastPushedSequence, event.sequence);
				values.push(event);
				notify();
			};

			const subscribe = (
				methodName: "SendMessage" | "RegenerateMessage" | "ResumeMessage" | "ResumeConversation",
				args: unknown[],
				isResume: boolean,
			): void => {
				const connection = nodeChatConnection.current();
				if (!connection) {
					return;
				}

				// Captured once per subscription: resumed events 0,1,2,… map to base, base+1, base+2,… directly
				// after the last sequence the original stream delivered. SignalR delivers in order within one
				// subscription, so the rebased stream stays contiguous for the guard. On a direct resume opening
				// (nothing pushed yet) the base is 0 and the rebase is the identity.
				const resumeSequenceBase = isResume && Number.isFinite(lastPushedSequence) ? lastPushedSequence + 1 : 0;

				activeSubscription = connection.stream<NodeChatStreamEventDto>(methodName, ...args).subscribe({
					next: (value) => {
						if (!isResume) {
							pushEvent(value);
							return;
						}
						// Resume events stamp the invocation id as the message id; remap to the assistant id
						// so the caller updates the same message instead of spawning a new one.
						pushEvent({
							...value,
							sequence: resumeSequenceBase + value.sequence,
							messageId: assistantMessageId ?? value.messageId,
						});
					},
					error: (error) => {
						activeSubscription = undefined;
						// Only a transport interruption is recoverable via resume: the run keeps going server-side
						// and onReconnected will re-subscribe. This applies to a resumed stream too — a second drop
						// resumes again (each resume rebases its restarted sequences past the new high-water mark).
						const recoverable =
							!signal.aborted &&
							!reachedTerminal &&
							!!invocationId &&
							(nodeChatConnection.status === "reconnecting" || nodeChatConnection.status === "connecting");
						if (recoverable) {
							notify();
							return;
						}
						// A ResumeMessage stream that errors while the connection is stable means the invocation is
						// unknown/terminal — the response already finished server-side. Complete cleanly so the caller
						// refetches the persisted conversation instead of showing a spurious failure.
						if (isResume) {
							completed = true;
							notify();
							return;
						}
						// A subscription error while the connection is stably connected is a genuine hub/application
						// failure (the invocation threw during turn setup before any terminal event) — fail fast so
						// the caller surfaces it instead of waiting for a resume that will never come.
						failure = error;
						completed = true;
						notify();
					},
					complete: () => {
						completed = true;
						notify();
					},
				});
			};

			/**
			 * Resynchronizes a stream that can no longer be repaired by appending — an offset gap, an overlap, or
			 * a server-sent reconcile. Re-entering through `ResumeMessage` is the whole repair: its first frame is
			 * an `assistant-snapshot` that replaces the client's accumulated text and re-bases the offsets, and the
			 * existing `resumeSequenceBase` rebase keeps its restarted numbering contiguous for the guard.
			 *
			 * Impossible before the invocation id is latched (the very first event latches it, and a delta can only
			 * mismatch against a position a prior frame established). If it ever is, the offending event is simply
			 * dropped and the turn's terminal — which carries the full text — converges the state.
			 */
			const repair = (): void => {
				if (completed || reachedTerminal || signal.aborted || !invocationId) {
					return;
				}
				activeSubscription?.dispose();
				activeSubscription = undefined;
				subscribe("ResumeMessage", [invocationId], true);
			};

			const resumeAfterReconnect = (): void => {
				if (completed || reachedTerminal || !invocationId || activeSubscription) {
					return;
				}
				subscribe("ResumeMessage", [invocationId], true);
			};

			const unsubscribeFromConnection = nodeChatConnection.subscribe({
				onReconnected: () => resumeAfterReconnect(),
				onClose: (error) => {
					if (!reachedTerminal && !signal.aborted) {
						failure = error ?? new Error("The local chat connection closed before the response completed.");
						completed = true;
						notify();
					}
				},
			});

			const abort = (): void => {
				activeSubscription?.dispose();
				activeSubscription = undefined;
				completed = true;
				notify();
			};

			signal.addEventListener("abort", abort, { once: true });

			await nodeChatConnection.ensureConnection();
			// The await above can resolve after an abort (or a close) already fired: re-check before subscribing so we
			// never open a subscription that abort()/the finally will not tear down, which would leak the subscription
			// and start a server run the caller has already given up on. Dispose any stray subscription and stop.
			if (signal.aborted || completed) {
				activeSubscription?.dispose();
				activeSubscription = undefined;
				completed = true;
			} else {
				subscribe(opening.method, opening.args, opening.isResumeOpening ?? false);
			}

			try {
				while (!completed || values.length > 0) {
					const value = values.shift();
					if (value) {
						yield value;
						continue;
					}

					if (failure) {
						throw failure;
					}

					// biome-ignore lint/performance/noAwaitInLoops: AsyncIterable bridge waits for the next SignalR push before yielding again.
					await new Promise<void>((resolve) => {
						wake = resolve;
					});
				}

				if (failure) {
					throw failure;
				}
			} finally {
				// The connection is shared and long-lived; only the per-send subscription and the reconnect
				// listener are torn down here. The persistent connection is reused for the next send.
				signal.removeEventListener("abort", abort);
				unsubscribeFromConnection();
				activeSubscription?.dispose();
			}
		},
	};
}
