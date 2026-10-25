// The 401 interceptor's way to the login page, without importing the router.
//
// `Interceptors.ts` sits on the axios instance's import path, and the router module pulls in the whole route tree,
// whose routes import the generated client, whose runtime config imports the axios instance back. A static router
// import there closed that cycle: whichever module entered it from the axios side built the hey-api client while
// `axiosInstance` was still unset, and the client silently fell back to a bare axios instance with no interceptors.
// So the router registers its navigation here when it is created (`Router.tsx`), and the interceptor calls it.
// Before registration (no router yet, nothing to redirect) the call is a no-op.

import { authPagePaths } from "@/core/auth/utils/RedirectPath";

type LoginNavigator = (redirect: string) => Promise<unknown>;

let navigator: LoginNavigator = async () => undefined;

export function registerLoginNavigator(next: LoginNavigator): void {
	navigator = next;
}

function navigateToLogin(redirect: string): Promise<unknown> {
	return navigator(redirect);
}

let isRedirectingToLogin = false;

/** Sends the page to sign-in, returning here afterwards; a no-op on an auth page or while a redirect is under way. */
export function redirectToLoginOnce(): void {
	if (authPagePaths.includes(globalThis.location.pathname) || isRedirectingToLogin) {
		return;
	}

	isRedirectingToLogin = true;
	Promise.resolve(navigateToLogin(globalThis.location.pathname + globalThis.location.search))
		.finally(() => {
			isRedirectingToLogin = false;
		})
		.catch(() => undefined);
}

// The vault-locked interceptor's way to the unlock page, registered alongside the login navigator for the same reason.
let vaultNavigator: LoginNavigator = async () => undefined;

export function registerVaultNavigator(next: LoginNavigator): void {
	vaultNavigator = next;
}

export function navigateToVault(redirect: string): Promise<unknown> {
	return vaultNavigator(redirect);
}
