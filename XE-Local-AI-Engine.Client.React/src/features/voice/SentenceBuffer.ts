// Accumulates streamed assistant text and flushes it to TTS as whole sentences — never token-by-token. Before every
// flush the text is sanitized so the synthesizer never reads code, raw URLs, or markdown link syntax aloud. Prose
// styling (bold/italic markers) is preserved untouched.

const MAX_SENTENCE_LENGTH = 512;
// Sentence terminators that trigger a flush: `!`/`?`/newline unconditionally, and `.` ONLY when followed by
// whitespace or the end of the buffer. The lookahead keeps periods inside URLs ("example.com") and decimals ("3.14")
// from producing false sentence breaks. (URLs are stripped during sanitization, but the boundary scan runs on the
// raw buffer, so the guard must live here too.)
const SENTENCE_BOUNDARY = /[!?\n]|\.(?=\s|$)/;

// Sanitization passes, applied in order. Order matters: fenced code before inline code, and markdown links before
// bare URLs (so the URL inside a link's parentheses is consumed by the link rule, leaving only the link text).
const FENCED_CODE = /```[\s\S]*?```/g;
const INLINE_CODE = /`[^`]*`/g;
const MARKDOWN_LINK = /\[([^\]]+)\]\([^)]+\)/g;
const BARE_URL = /\b(?:https?|ftp):\/\/[^\s)]+/gi;
const COLLAPSE_WHITESPACE = /\s+/g;

/**
 * Strips code, URLs, and markdown link syntax from a flushable text segment while keeping the prose (bold/italic
 * markers are left in place). Returns the collapsed, trimmed result — possibly empty when the segment was pure code.
 */
export function sanitizeForSpeech(text: string): string {
	return text
		.replace(FENCED_CODE, " ")
		.replace(INLINE_CODE, " ")
		.replace(MARKDOWN_LINK, "$1")
		.replace(BARE_URL, " ")
		.replace(COLLAPSE_WHITESPACE, " ")
		.trim();
}

/**
 * Stateful buffer for one streamed assistant turn. Feed raw text deltas via `push`; it returns the sentences ready to
 * speak (sanitized, non-empty). Call `flush` once the stream ends to emit the trailing remainder.
 */
export class SentenceBuffer {
	private buffer = "";

	/**
	 * Appends a streamed delta and returns every sentence now flushable: each complete sentence (up to and including a
	 * `. ! ? \n` boundary) or, when no boundary has arrived yet, a hard cut at the 512-char cap. Sanitized; empties
	 * (pure-code/URL segments) are dropped.
	 */
	push(delta: string): string[] {
		this.buffer += delta;
		const flushed: string[] = [];

		let segment = this.takeNextSegment();
		while (segment !== undefined) {
			const sanitized = sanitizeForSpeech(segment);
			if (sanitized.length > 0) {
				flushed.push(sanitized);
			}

			segment = this.takeNextSegment();
		}

		return flushed;
	}

	/** Emits the buffered remainder (sanitized) at end-of-stream, or undefined when nothing speakable remains. */
	flush(): string | undefined {
		if (this.buffer.length === 0) {
			return undefined;
		}

		const remainder = this.buffer;
		this.buffer = "";
		const sanitized = sanitizeForSpeech(remainder);
		return sanitized.length > 0 ? sanitized : undefined;
	}

	/** Discards all buffered text (barge-in / new turn). */
	reset(): void {
		this.buffer = "";
	}

	// Pulls the next flushable segment off the buffer, or undefined when the buffer holds only an in-progress sentence
	// shorter than the cap. Flush precedence: earliest sentence boundary first, otherwise the 512-char cap.
	private takeNextSegment(): string | undefined {
		const boundary = SENTENCE_BOUNDARY.exec(this.buffer);
		if (boundary) {
			const end = boundary.index + 1;
			const segment = this.buffer.slice(0, end);
			this.buffer = this.buffer.slice(end);
			return segment;
		}

		if (this.buffer.length >= MAX_SENTENCE_LENGTH) {
			const segment = this.buffer.slice(0, MAX_SENTENCE_LENGTH);
			this.buffer = this.buffer.slice(MAX_SENTENCE_LENGTH);
			return segment;
		}

		return undefined;
	}
}
