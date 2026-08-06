// Shared byte-count formatter. Lives in core because `model-fit` (hardware-profile figures) and `models` (GGUF
// quant/file sizes) both need the identical GB rendering — neither feature owns the concept.

// Formats a raw byte count as a compact GB string (one decimal), or a dash when absent.
export function formatBytesAsGb(bytes: number | null): string {
	if (bytes === null) {
		return "—";
	}
	return `${(bytes / 1024 ** 3).toFixed(1)} GB`;
}
