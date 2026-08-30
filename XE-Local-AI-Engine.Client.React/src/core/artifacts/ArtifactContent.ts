// Artifact bodies cross the wire the same way for every family that stores them: allowlisted text media types are sent
// as-is and everything else is base64. One decoder, because the two viewers that read them (work sessions, development
// workflows) had byte-identical copies and a fix to one would silently miss the other.
//
// The Monaco LANGUAGE choice deliberately stays per feature: each one has its own artifact-kind vocabulary and its own
// honest default (markdown for a workflow document, plaintext for a session's), so a shared function would have to take
// both vocabularies and pick between two defaults.

/**
 * Decodes an artifact body for the read-only editor. `fatal` is what turns "this is not text" into an error instead of
 * a page full of replacement characters, so the caller can say "binary, no preview" rather than render mojibake.
 */
export function decodeArtifactContent(content: string, isBase64: boolean): { text: string; isBinary: boolean } {
	if (!isBase64) {
		return { text: content, isBinary: false };
	}
	try {
		const binary = atob(content);
		const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
		return { text: new TextDecoder("utf-8", { fatal: true }).decode(bytes), isBinary: false };
	} catch {
		return { text: "", isBinary: true };
	}
}
