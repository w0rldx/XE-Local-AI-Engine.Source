export interface CodeEditorProps {
	readonly value: string;
	/** Monaco language id (`diff`, `json`, `markdown`, `csharp`, `typescript`, `shell`, `yaml`, …). Default `plaintext`. */
	readonly language?: string;
	readonly readOnly?: boolean;
	/** Omit for a pure viewer. Fires with the full document on every edit. */
	readonly onChange?: (value: string) => void;
	/** CSS height of the editor surface. Default 320px. */
	readonly height?: number | string;
	readonly wordWrap?: boolean;
	/**
	 * Inspection mode for untrusted text: forces read-only and renders whitespace, control characters and invisible or
	 * ambiguous Unicode visibly. Fails closed: if the editor cannot load, an alert replaces the content instead of a
	 * plain-text fallback that would hide exactly those characters.
	 */
	readonly inspect?: boolean;
	readonly "aria-label"?: string;
	readonly "data-testid"?: string;
}
