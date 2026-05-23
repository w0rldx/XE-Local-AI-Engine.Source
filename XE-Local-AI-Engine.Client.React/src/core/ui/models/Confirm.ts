export interface ConfirmOptions {
	title?: string;
	description?: string;
	confirmationText?: string;
	cancellationText?: string;
}

export interface ConfirmContextType {
	confirm: (options: ConfirmOptions) => Promise<boolean>;
}
