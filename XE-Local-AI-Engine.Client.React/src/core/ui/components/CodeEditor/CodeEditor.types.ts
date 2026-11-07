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
	/** 1-based, inclusive line ranges drawn as highlighted whole lines. The plain-text fallback ignores them. */
	readonly highlightLines?: readonly { readonly start: number; readonly end: number }[];
	/** 1-based line scrolled to the middle of the view whenever it (or the document) changes. */
	readonly revealLine?: number;
	readonly "aria-label"?: string;
	readonly "data-testid"?: string;
}
