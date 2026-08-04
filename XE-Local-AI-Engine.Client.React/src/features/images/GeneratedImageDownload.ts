// Saves a generated PNG to the user's disk. The bytes are already in memory as a blob (useImageObjectUrl fetches them
// through the authed axios instance), so the download is a pure client-side anchor click — no second request, and the
// Operator-gated retrieve endpoint is never exposed to a bare <a href> that could not carry the bearer token.

/** Max length of the model-name segment in a download filename; keeps the name well inside every filesystem's limit. */
const MaxModelSegmentLength = 40;

/**
 * Builds a stable, filesystem-safe download name for a generated image, e.g. `xe-image-sd-1.5-seed-182736.png`.
 *
 * The model name is operator-supplied free text, so every character outside `[A-Za-z0-9._-]` collapses to a single
 * dash: a name carrying a path separator must never steer where the browser writes the file. Dots are kept (real model
 * names contain them, e.g. `sd-1.5`) but a run of them collapses to one, so no `..` traversal segment survives the
 * separator stripping. Truncation happens before the final trim so a cut landing on a separator cannot leave a
 * trailing dash.
 */
export function buildGeneratedImageFileName(modelName: string, seed: number): string {
	const safeModel = modelName
		.replaceAll(/[^a-zA-Z0-9._-]+/g, "-")
		.replaceAll(/\.{2,}/g, ".")
		.slice(0, MaxModelSegmentLength)
		.replaceAll(/^[-._]+|[-._]+$/g, "")
		.toLowerCase();
	const modelSegment = safeModel.length > 0 ? `${safeModel}-` : "";
	// A negative seed is the "runtime picked one and did not tell us" sentinel, not a seed. Naming a file
	// `…-seed--1.png` would advertise a value that reproduces nothing, so the segment is dropped entirely.
	const seedSegment = seed >= 0 ? `seed-${seed}` : "random";
	return `xe-image-${modelSegment}${seedSegment}.png`;
}

/** Triggers a browser download of `blob` under `fileName`. */
export function downloadGeneratedImage(blob: Blob, fileName: string): void {
	const url = URL.createObjectURL(blob);
	try {
		const anchor = document.createElement("a");
		anchor.href = url;
		anchor.download = fileName;
		document.body.append(anchor);
		anchor.click();
		anchor.remove();
	} finally {
		URL.revokeObjectURL(url);
	}
}
