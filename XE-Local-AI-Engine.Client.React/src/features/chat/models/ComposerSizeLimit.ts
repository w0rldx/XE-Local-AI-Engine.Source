// Client-side mirror of the node's message-size cap (Security:MaxMessageSizeKb, enforced server-side in
// LocalChatHub.EnsureMessageWithinSizeCap before anything is persisted). Purely advisory: it exists so the composer
// can warn — and refuse to send — instead of firing a request the hub is guaranteed to reject with a HubException.
// The server stays the only enforcement point, so an unknown limit (the conversation list has not loaded yet, or an
// older node omits the field) simply means no pre-check.

// Fraction of the limit a draft must pass before the size readout appears. Below it the number is noise — nobody
// pasting a paragraph wants a byte counter.
export const composerSizeIndicatorThreshold = 0.8;

// UTF-8 costs at most 3 bytes per UTF-16 code unit (an astral code point is 4 bytes across 2 units, so 2 per unit),
// which makes `text.length * 3` an exact upper bound on the encoded size. Every ordinary-length draft fails this
// test, so the encoder below never runs on a keystroke — no debounce needed.
const maxUtf8BytesPerCodeUnit = 3;

export interface ComposerSizeState {
	// UTF-8 byte count of the outgoing content, measured the same way the server measures it.
	readonly bytes: number;
	readonly limitBytes: number;
	readonly overLimit: boolean;
}

export function utf8ByteLength(text: string): number {
	return new TextEncoder().encode(text).length;
}

// Rounded UP, mirroring the server's rejection message, so a draft reported as "N KB" is never at or below the
// stated limit.
export function toDisplayKb(bytes: number): number {
	return Math.ceil(bytes / 1024);
}

/**
 * Size state for the draft, or `undefined` when there is nothing to say — either the limit is unknown (no
 * pre-check) or the draft is comfortably under it. Both cases render no indicator and block no send, so they
 * deliberately collapse to one shape.
 *
 * `text` must be the content that will actually be SENT (the trimmed draft), not the raw textarea value.
 */
export function evaluateComposerSize(text: string, maxMessageSizeKb: number | undefined): ComposerSizeState | undefined {
	if (maxMessageSizeKb === undefined || maxMessageSizeKb <= 0) {
		return undefined;
	}

	const limitBytes = maxMessageSizeKb * 1024;
	const indicatorBytes = limitBytes * composerSizeIndicatorThreshold;
	if (text.length * maxUtf8BytesPerCodeUnit < indicatorBytes) {
		return undefined;
	}

	const bytes = utf8ByteLength(text);
	if (bytes < indicatorBytes) {
		return undefined;
	}

	return { bytes, limitBytes, overLimit: bytes > limitBytes };
}
