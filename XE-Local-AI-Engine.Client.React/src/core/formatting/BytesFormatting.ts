// Shared byte-count formatters. They live in core because `model-fit` (hardware-profile figures), `models` (GGUF
// quant/file sizes), `images`, `node-settings` and `transcription` all need identical size rendering — no single
// feature owns the concept, and a feature reaching into another feature's helper for it is architecture debt the
// dependency baseline refuses.

import i18next from "i18next";

// No grouping, so a size reads exactly as the old `toFixed(1)` did in English ("1234.5 GB", never "1,234.5 GB").
const oneDecimal: Intl.NumberFormatOptions = { minimumFractionDigits: 1, maximumFractionDigits: 1, useGrouping: false };

// The GB figure's decimal separator follows the ACTIVE UI LANGUAGE ("3.6 GB" / "3,6 GB"), read from the i18next
// singleton for the same reasons `TimeFormatting` gives: the requested `language` first, and a malformed stored tag
// (`en_US`) is a RangeError that falls back to the environment default instead of throwing once per table row.
function formatGb(gigabytes: number): string {
	try {
		return `${gigabytes.toLocaleString(i18next.language ?? i18next.resolvedLanguage, oneDecimal)} GB`;
	} catch {
		return `${gigabytes.toLocaleString(undefined, oneDecimal)} GB`;
	}
}

// Formats a raw byte count as a compact GB string (one decimal, in the UI language), or a dash when absent.
export function formatBytesAsGb(bytes: number | null): string {
	if (bytes === null) {
		return "—";
	}
	return formatGb(bytes / 1024 ** 3);
}

/**
 * Formats a byte count into a compact size (e.g. "324 MB", "1.2 GB"; "1,2 GB" in German). Shared by every
 * byte-progress surface — the GGUF download panel, the operations feed, the image model manager and the
 * whisper model catalog — so they never drift into disagreeing about how a transfer's size reads. Deliberately NOT
 * {@link formatBytesAsGb}, which is GB-only and would render a 78 MB whisper weight as "0.1 GB". Only the GB decimal
 * follows the UI language; MB, KB and B are whole numbers and read the same in every language.
 */
export function humanizeBytes(bytes: number): string {
	if (bytes >= 1_073_741_824) {
		return formatGb(bytes / 1_073_741_824);
	}
	if (bytes >= 1_048_576) {
		return `${Math.round(bytes / 1_048_576)} MB`;
	}
	if (bytes >= 1024) {
		return `${Math.round(bytes / 1024)} KB`;
	}
	return `${bytes} B`;
}
