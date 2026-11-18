export interface ConfirmOptions {
	title?: string;
	description?: string;
	confirmationText?: string;
	cancellationText?: string;
	/** Runs once the dialog has finished closing (after its exit transition, unmounted), for focus fallbacks. */
	onClosed?: () => void;
}

export interface ConfirmContextType {
	confirm: (options: ConfirmOptions) => Promise<boolean>;
}
