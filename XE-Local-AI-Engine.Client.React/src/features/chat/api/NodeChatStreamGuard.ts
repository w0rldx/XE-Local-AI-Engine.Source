import type { NodeChatStreamEventDto } from "@/features/chat/models/NodeChatStreamTypes";

export type StreamWatchdogCategory = "no-first-chunk" | "inter-chunk-stall";

/* eslint-disable react-doctor/async-await-in-loop */

/**
 * Raised when the client-side watchdog gives up on a stalled stream. The category distinguishes a stream that
 * never produced a first chunk from one that went silent mid-flight so the UI can label the failure.
 */
export class StreamWatchdogError extends Error {
	readonly category: StreamWatchdogCategory;

	constructor(category: StreamWatchdogCategory, message: string) {
		super(message);
		this.name = "StreamWatchdogError";
		this.category = category;
	}
}

export interface StreamGuardOptions {
	// Max wait for the first event after the stream starts.
	firstChunkTimeoutMs?: number;
	// Max wait between consecutive events once streaming has begun.
	interChunkTimeoutMs?: number;
}

// Large values are intentional: big local models (20B+, F16 quant) can take well over 30 s to produce
// the first token during cold prompt processing, and reasoning models can pause silently between answer
// chunks for many seconds — the server has no client-visible heartbeat during that gap. These defaults
// must be conservative enough to survive the worst-case generation cadence on modest hardware.
const defaultFirstChunkTimeoutMs = 120_000;
const defaultInterChunkTimeoutMs = 180_000;

interface PendingEvent {
	value: NodeChatStreamEventDto;
}

const watchdogTripped = Symbol("watchdog-tripped");

// Resolves (never rejects) with a sentinel when the deadline elapses, so racing it against the next event can
// never leave a dangling rejected promise. The caller throws the categorized error when the sentinel wins.
function watchdogTimer(ms: number): { signal: Promise<typeof watchdogTripped>; cancel: () => void } {
	let handle: ReturnType<typeof setTimeout> | undefined;
	const signal = new Promise<typeof watchdogTripped>((resolve) => {
		handle = setTimeout(() => resolve(watchdogTripped), ms);
	});
	return {
		signal,
		cancel: () => {
			if (handle) {
				clearTimeout(handle);
			}
		},
	};
}

/**
 * Wraps a chat stream with two guarantees:
 *  - events are emitted in ascending `sequence` order (out-of-order arrivals are buffered until the gap fills);
 *  - a watchdog fails the stream if the first chunk never arrives, or if it stalls between chunks.
 */
export function guardNodeChatStream(
	source: AsyncIterable<NodeChatStreamEventDto>,
	options: StreamGuardOptions = {},
): AsyncIterable<NodeChatStreamEventDto> {
	const firstChunkTimeoutMs = options.firstChunkTimeoutMs ?? defaultFirstChunkTimeoutMs;
	const interChunkTimeoutMs = options.interChunkTimeoutMs ?? defaultInterChunkTimeoutMs;

	return {
		async *[Symbol.asyncIterator](): AsyncIterator<NodeChatStreamEventDto> {
			const iterator = source[Symbol.asyncIterator]();
			// Out-of-order buffer keyed by sequence; emitted once the next expected sequence is contiguous.
			const buffered = new Map<number, PendingEvent>();
			let nextSequence = Number.NEGATIVE_INFINITY;
			let received = false;

			try {
				while (true) {
					const category: StreamWatchdogCategory = received ? "inter-chunk-stall" : "no-first-chunk";
					const watchdog = watchdogTimer(received ? interChunkTimeoutMs : firstChunkTimeoutMs);
					// Keep a reference so the losing branch of the race never surfaces as an unhandled rejection if
					// it settles after the watchdog has already won.
					const nextEvent = iterator.next();
					nextEvent.catch(() => undefined);
					let raced: IteratorResult<NodeChatStreamEventDto> | typeof watchdogTripped;
					try {
						// biome-ignore lint/performance/noAwaitInLoops: each event must be awaited (and watchdog-raced) before the next.
						raced = await Promise.race([nextEvent, watchdog.signal]);
					} finally {
						watchdog.cancel();
					}

					if (raced === watchdogTripped) {
						throw new StreamWatchdogError(category, `Local chat stream timed out (${category}).`);
					}

					const result = raced;
					if (result.done) {
						break;
					}

					received = true;
					const event = result.value;
					if (nextSequence === Number.NEGATIVE_INFINITY) {
						nextSequence = event.sequence;
					}

					if (event.sequence < nextSequence) {
						// A stale duplicate from a reconnect replay — already applied, skip it.
						continue;
					}

					buffered.set(event.sequence, { value: event });
					while (buffered.has(nextSequence)) {
						const pending = buffered.get(nextSequence);
						buffered.delete(nextSequence);
						nextSequence += 1;
						if (pending) {
							yield pending.value;
						}
					}
				}

				// Flush any trailing buffered events whose preceding gap never filled (best-effort, ordered).
				for (const sequence of [...buffered.keys()].toSorted((left, right) => left - right)) {
					const pending = buffered.get(sequence);
					if (pending) {
						yield pending.value;
					}
				}
			} finally {
				// Signal the source to dispose without blocking error propagation: a source suspended on a
				// never-settling await (e.g. a stalled stream the watchdog just failed) would otherwise hang here.
				const disposed = Promise.resolve(iterator.return?.());
				disposed.catch(() => undefined);
			}
		},
	};
}
