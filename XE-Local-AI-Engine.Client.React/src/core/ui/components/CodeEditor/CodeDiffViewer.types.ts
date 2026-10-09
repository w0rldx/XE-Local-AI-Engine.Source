export interface CodeDiffViewerProps {
	/** The baseline text, shown on the left (or as removals inline). */
	readonly original: string;
	/** The revised text, shown on the right (or as additions inline). */
	readonly modified: string;
	/** Monaco language id for both sides. Default `plaintext`. */
	readonly language?: string;
	/** CSS height of the viewer surface. Default 320px. */
	readonly height?: number | string;
	readonly "aria-label"?: string;
	readonly "data-testid"?: string;
}
