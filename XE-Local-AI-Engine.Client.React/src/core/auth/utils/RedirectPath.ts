// The sign-in and unlock pages: never a place to return to after signing in or unlocking.
export const authPagePaths: readonly string[] = ["/login", "/setup", "/vault", "/vault-setup"];

export function getSafeRedirectPath(value: string | undefined, fallback = "/"): string {
	if (!value?.startsWith("/")) {
		return fallback;
	}

	// Browsers read "//host" and "/\host" as another origin.
	if (value.startsWith("//") || value.startsWith("/\\")) {
		return fallback;
	}

	const pathname = value.split(/[?#]/, 1)[0] ?? value;
	if (authPagePaths.includes(pathname)) {
		return fallback;
	}

	return value;
}
