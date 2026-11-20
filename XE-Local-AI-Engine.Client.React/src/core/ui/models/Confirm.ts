/** Set on ConfirmProvider's one dialog, so a caller can tell it (closing, or about to be reused) from any other dialog. */
export const CONFIRM_DIALOG_ATTRIBUTE = "data-confirm-dialog";

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
	/**
	 * True while a confirmation awaits its answer. A new `confirm` replaces that answer's resolver, so a flow opening one
	 * late checks this first. Optional so hand-rolled test contexts stay valid; absent means none is pending.
	 */
	hasPending?: () => boolean;
}
