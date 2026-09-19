// Shared byte-count formatters. They live in core because `model-fit` (hardware-profile figures), `models` (GGUF
// quant/file sizes), `images`, `node-settings` and `transcription` all need identical size rendering — no single
// feature owns the concept, and a feature reaching into another feature's helper for it is architecture debt the
// dependency baseline refuses.

// Formats a raw byte count as a compact GB string (one decimal), or a dash when absent.
export function formatBytesAsGb(bytes: number | null): string {
	if (bytes === null) {
		return "—";
	}
	return `${(bytes / 1024 ** 3).toFixed(1)} GB`;
}

/**
 * Formats a byte count into a compact, locale-neutral size (e.g. "324 MB", "1.2 GB"). Shared by every byte-progress
 * surface — the GGUF download panel, the llama.cpp runtime-acquisition banner, the image model manager and the
 * whisper model catalogue — so they never drift into disagreeing about how a transfer's size reads. Deliberately NOT
 * {@link formatBytesAsGb}, which is GB-only and would render a 78 MB whisper weight as "0.1 GB".
 */
export function humanizeBytes(bytes: number): string {
	if (bytes >= 1_073_741_824) {
		return `${(bytes / 1_073_741_824).toFixed(1)} GB`;
	}
	if (bytes >= 1_048_576) {
		return `${Math.round(bytes / 1_048_576)} MB`;
	}
	if (bytes >= 1024) {
		return `${Math.round(bytes / 1024)} KB`;
	}
	return `${bytes} B`;
}
