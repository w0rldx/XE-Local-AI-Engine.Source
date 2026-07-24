// rrweb DOM-replay recorder — Developer-Mode-only, privacy-masked.
//
// rrweb is loaded via a dynamic `import("rrweb")` so the library is code-split OUT of the main
// bundle and is never fetched unless Developer Mode is on. The recording config is PINNED for
// privacy: rendered DOM text is masked (`maskTextSelector: "*"` + `maskTextFn`),
// not just inputs, so on-screen conversation text never reaches a packed segment.
//
// Packing: rrweb 2.0.1 no longer exports `pack` (it moved to the separate `@rrweb/packer` package,
// which is not installed). We re-implement the same wire format here — the `@rrweb/packer` "v1"
// scheme (fflate zlib of the JSON event, tagged `v: "v1"`) — so a packed segment round-trips with
// `@rrweb/packer`'s `unpack` (or `unpackRrwebEvent` below) for replay in the diagnostics panel.

import { strFromU8, strToU8, unzlibSync, zlibSync } from "fflate";

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import type { RrwebPackedEvent } from "@/core/diagnostics/Types";

/** Tag written by `@rrweb/packer` so `unpack` can detect a packed (vs already-plain) event. */
const PACK_MARK = "v1";

/** Minimal shape of an rrweb event we need for packing (full event type lives in `@rrweb/types`). */
interface RrwebEvent {
	readonly type: number;
	readonly data: unknown;
	readonly timestamp: number;
}

/**
 * Pack a single rrweb event into the `@rrweb/packer` "v1" string format (zlib + JSON, tagged).
 * Exported for the privacy round-trip test and for the diagnostics panel's replay decoding.
 */
export function packRrwebEvent(event: RrwebEvent): RrwebPackedEvent {
	const tagged = { ...event, v: PACK_MARK };
	return strFromU8(zlibSync(strToU8(JSON.stringify(tagged))), true);
}

/** Inverse of {@link packRrwebEvent}; throws if `raw` was not produced by the "v1" packer. */
export function unpackRrwebEvent(raw: RrwebPackedEvent): RrwebEvent {
	const decoded = JSON.parse(strFromU8(unzlibSync(strToU8(raw, true)), false)) as RrwebEvent & { v?: string };
	if (decoded.v !== PACK_MARK) {
		throw new Error("Unrecognised rrweb pack format");
	}
	return { type: decoded.type, data: decoded.data, timestamp: decoded.timestamp };
}

/** Subset of rrweb's `recordOptions` we pin; typed locally to avoid an `any` from the dynamic import. */
interface RrwebRecordOptions {
	readonly emit: (event: unknown, isCheckout?: boolean) => void;
	readonly packFn: (event: RrwebEvent) => string;
	readonly checkoutEveryNms: number;
	readonly maskAllInputs: boolean;
	readonly maskTextSelector: string;
	readonly maskTextFn: (text: string) => string;
	readonly blockClass: string;
}

type RrwebStopFn = () => void;

interface RrwebModule {
	readonly record: (options: RrwebRecordOptions) => RrwebStopFn | undefined;
}

/** Keep at most the current + previous checkout segment so a capture always has a full recent run. */
const MAX_SEGMENTS = 2;
/** Backstop so the in-memory ring stays flat even if checkouts are sparse (~2 MB of packed chars). */
const MAX_TOTAL_CHARS = 2_000_000;

let stopFn: RrwebStopFn | undefined;
let starting = false;
/** Bounded ring of packed events, partitioned into checkout-delimited segments (newest last). */
let segments: RrwebPackedEvent[][] = [[]];

function totalChars(): number {
	let total = 0;
	for (const segment of segments) {
		for (const event of segment) {
			total += event.length;
		}
	}
	return total;
}

function enforceCaps(): void {
	while (segments.length > MAX_SEGMENTS) {
		segments.shift();
	}
	while (totalChars() > MAX_TOTAL_CHARS && segments.length > 1) {
		segments.shift();
	}
	// Last resort: a single oversized segment — drop its oldest events (keep at least one).
	const current = segments.at(-1);
	while (current && totalChars() > MAX_TOTAL_CHARS && current.length > 1) {
		current.shift();
	}
}

function handleEmit(event: unknown, isCheckout?: boolean): void {
	// `packFn` always yields a string; guard defensively rather than trust the dynamic-import type.
	if (typeof event !== "string") {
		return;
	}
	if (isCheckout) {
		segments.push([]);
	}
	segments.at(-1)?.push(event);
	enforceCaps();
}

/**
 * Start rrweb recording. No-op unless Developer Mode is on. Idempotent and safe
 * to call repeatedly; the rrweb chunk is only fetched here, behind the Developer-Mode gate.
 */
export async function startRrwebRecording(): Promise<void> {
	if (!useDeveloperModeStore.getState().developerMode) {
		return;
	}
	if (stopFn || starting) {
		return;
	}
	starting = true;
	try {
		const rrweb = (await import("rrweb")) as unknown as RrwebModule;
		// Re-check after the async import in case the gate was turned off meanwhile.
		if (stopFn || !useDeveloperModeStore.getState().developerMode) {
			return;
		}
		segments = [[]];
		stopFn = rrweb.record({
			emit: handleEmit,
			packFn: packRrwebEvent,
			checkoutEveryNms: 30_000,
			maskAllInputs: true,
			maskTextSelector: "*",
			maskTextFn: (text) => "•".repeat(text.length),
			blockClass: "rr-block",
		});
	} finally {
		starting = false;
	}
}

/** Tear down recording (calls rrweb's stop handler) and drop the in-memory segment. */
export function stopRrwebRecording(): void {
	if (stopFn) {
		stopFn();
		stopFn = undefined;
	}
	segments = [[]];
}

/**
 * The current bounded, packed rrweb segment for the snapshot bundler to attach to a {@link Snapshot}.
 * Returns an empty array when Developer Mode is off / recording is not active.
 */
export function getRrwebSegment(): RrwebPackedEvent[] {
	return segments.flat();
}
