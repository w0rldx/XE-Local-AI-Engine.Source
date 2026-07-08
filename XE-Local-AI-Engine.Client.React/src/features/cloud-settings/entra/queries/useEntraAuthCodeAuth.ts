// TanStack Query hooks for the Entra ID authorization-code sign-in endpoints on the stored EntraId Azure Foundry
// connection. The status query is polled while a sign-in is pending; polling stops once a terminal state
// (Succeeded/Failed) is observed, an error is detected, or the caller disables it. Mirrors useEntraDeviceCodeAuth.ts.

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	entraAuthCodeSignInMutation,
	entraAuthCodeStatusOptions,
	entraAuthCodeStatusQueryKey,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

const POLL_INTERVAL_MS = 2_000;
// Fallback stop for polling if the response's own expiry is ever missing.
const MAX_POLL_MS = 5 * 60 * 1_000;

interface UseEntraAuthCodeStatusOptions {
	/** When true the hook polls until the sign-in reaches a terminal state or the timeout elapses. */
	polling: boolean;
	/** Absolute timestamp (Date.now()) at which polling began. Undefined = not started. */
	pollStartedAt?: number;
}

export function useEntraAuthCodeStatus(options: UseEntraAuthCodeStatusOptions) {
	const { polling, pollStartedAt } = options;

	const timedOut = polling && pollStartedAt !== undefined && Date.now() - pollStartedAt > MAX_POLL_MS;

	return useQuery({
		...withResponseValidation(entraAuthCodeStatusOptions()),
		// Poll while pending and not yet timed out; stop once we know the final state.
		refetchInterval: polling && !timedOut ? POLL_INTERVAL_MS : false,
		// Status endpoint is always fresh — never serve a stale cached value during polling.
		staleTime: 0,
	});
}

export function useEntraAuthCodeSignIn(onStarted: (authorizeUrl: string, expiresAtUtc: string) => void) {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(entraAuthCodeSignInMutation()),
		onSuccess: async (data) => {
			onStarted(data.authorizeUrl, data.expiresAtUtc);
			// Force the first status poll to refetch rather than serving a pre-start cached "None" state.
			await queryClient.invalidateQueries({ queryKey: entraAuthCodeStatusQueryKey() });
		},
	});
}
