export function getErrorMessageText(text: unknown): string | undefined {
	if (typeof text === "string") {
		return text;
	}

	if (text && typeof text === "object" && "message" in text && typeof text.message === "string") {
		return text.message;
	}

	return undefined;
}
