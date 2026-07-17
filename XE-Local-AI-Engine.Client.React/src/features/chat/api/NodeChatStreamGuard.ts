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

// Extended inter-event deadline used ONLY while the server reports a pre-first-token cold-load phase
// (preparing_runtime / loading_model). During a cold model load the wire goes fully silent — the server
// emits the phase events back-to-back and then blocks on the load with no heartbeat — so the normal
// 180 s inter-chunk deadline would false-fail a legitimate load. The backend caps readiness at 600 s
// (LlamaServerSupervisorOptions size-aware readiness ceiling), so this must exceed that plus margin, or a
// slow-but-valid load trips the watchdog before the server itself would give up. Reverts to the normal
// inter-chunk deadline the moment a `generating` phase or the first content/reasoning delta arrives.
const coldLoadInterEventTimeoutMs = 660_000;

// Wire-string literals mirroring nodeChatStreamEventTypes / the runtimePhase union
// (NodeChatStreamState.ts / NodeChatStreamTypes.ts). Duplicated here rather than imported to keep the
// guard free of the stream-state module's heavy import graph; keep in sync with those sources.
const assistantPhaseEventType = "assistant-phase";
const runtimePhasePreparingRuntime = "preparing_runtime";
const runtimePhaseLoadingModel = "loading_model";
const runtimePhaseGenerating = "generating";

// A cold-load phase is in effect while the latest reported phase is a pre-first-token load stage.
function isColdLoadPhase(phase: string | undefined): boolean {
	return phase === runtimePhasePreparingRuntime || phase === runtimePhaseLoadingModel;
}

// The first streamed content or reasoning delta marks the end of the cold load: generation has begun.
function hasGenerationOutput(event: NodeChatStreamEventDto): boolean {
	return (
		(typeof event.delta === "string" && event.delta.length > 0) ||
		(typeof event.reasoningDelta === "string" && event.reasoningDelta.length > 0)
	);
}

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
			// Latest reported runtime phase and whether generation has begun — together they widen the
			// inter-event deadline across a silent cold model load, then snap it back once tokens flow.
			let latestPhase: string | undefined;
			let generationStarted = false;

			try {
				while (true) {
					const category: StreamWatchdogCategory = received ? "inter-chunk-stall" : "no-first-chunk";
					let deadlineMs: number;
					if (!received) {
						deadlineMs = firstChunkTimeoutMs;
					} else if (!generationStarted && isColdLoadPhase(latestPhase)) {
						deadlineMs = coldLoadInterEventTimeoutMs;
					} else {
						deadlineMs = interChunkTimeoutMs;
					}
					const watchdog = watchdogTimer(deadlineMs);
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
					// Track the cold-load phase off the wire (not the ordered stream): the watchdog reacts to
					// physical arrival, so update from every event before the sequence gate below may skip it.
					if (event.type === assistantPhaseEventType && typeof event.runtimePhase === "string") {
						latestPhase = event.runtimePhase;
					}
					if (!generationStarted && (latestPhase === runtimePhaseGenerating || hasGenerationOutput(event))) {
						generationStarted = true;
					}
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
