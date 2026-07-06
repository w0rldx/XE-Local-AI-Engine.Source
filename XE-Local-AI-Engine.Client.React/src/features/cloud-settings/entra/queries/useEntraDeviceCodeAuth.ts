// TanStack Query hooks for the Entra ID device-code sign-in endpoints on the stored EntraId Azure Foundry
// connection. The status query is polled while a sign-in is pending; polling stops once a terminal state
// (Succeeded/Failed) is observed, an error is detected, or the caller disables it. Mirrors useCodexAuth.ts.

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	entraDeviceCodeSignInMutation,
	entraDeviceCodeStatusOptions,
	entraDeviceCodeStatusQueryKey,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

const POLL_INTERVAL_MS = 2_000;
// Fallback stop for polling if the device code's own expiry is ever missing from a response.
const MAX_POLL_MS = 5 * 60 * 1_000;

interface UseEntraDeviceCodeStatusOptions {
	/** When true the hook polls until the sign-in reaches a terminal state or the timeout elapses. */
	polling: boolean;
	/** Absolute timestamp (Date.now()) at which polling began. Undefined = not started. */
	pollStartedAt?: number;
}

export function useEntraDeviceCodeStatus(options: UseEntraDeviceCodeStatusOptions) {
	const { polling, pollStartedAt } = options;

	const timedOut = polling && pollStartedAt !== undefined && Date.now() - pollStartedAt > MAX_POLL_MS;

	return useQuery({
		...withResponseValidation(entraDeviceCodeStatusOptions()),
		// Poll while pending and not yet timed out; stop once we know the final state.
		refetchInterval: polling && !timedOut ? POLL_INTERVAL_MS : false,
		// Status endpoint is always fresh — never serve a stale cached value during polling.
		staleTime: 0,
	});
}

export function useEntraDeviceCodeSignIn(onStarted: (userCode: string, verificationUri: string, expiresAtUtc: string) => void) {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(entraDeviceCodeSignInMutation()),
		onSuccess: async (data) => {
			onStarted(data.userCode, data.verificationUri, data.expiresAtUtc);
			// Force the first status poll to refetch rather than serving a pre-start cached "None" state.
			await queryClient.invalidateQueries({ queryKey: entraDeviceCodeStatusQueryKey() });
		},
	});
}
