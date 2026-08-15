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
	readonly "aria-label"?: string;
	readonly "data-testid"?: string;
}
