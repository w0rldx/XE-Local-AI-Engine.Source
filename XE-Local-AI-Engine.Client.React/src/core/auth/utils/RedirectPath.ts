export function getSafeRedirectPath(value: string | undefined, fallback = "/"): string {
	if (!value || !value.startsWith("/")) {
		return fallback;
	}

	if (value.startsWith("//")) {
		return fallback;
	}

	return value;
}
