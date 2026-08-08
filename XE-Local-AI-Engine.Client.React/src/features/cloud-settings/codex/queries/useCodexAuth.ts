// TanStack Query hooks for the Codex OAuth endpoints. The status query is polled while a login is
// pending; polling stops once the user is signed in, an error is detected, or the caller disables it.

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	codexLoginMutation,
	codexLogoutMutation,
	codexStatusOptions,
	codexStatusQueryKey,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toast } from "@/core/ui/notifications/Toast";

const POLL_INTERVAL_MS = 2_000;
// Stop polling after 5 minutes — matches the loopback listener lifetime on the backend.
const MAX_POLL_MS = 5 * 60 * 1_000;

interface UseCodexStatusOptions {
	/** When true the hook polls until sign-in completes or the timeout elapses. */
	polling: boolean;
	/** Absolute timestamp (Date.now()) at which polling began. Undefined = not started. */
	pollStartedAt?: number;
}

export function useCodexStatus(options: UseCodexStatusOptions) {
	const { polling, pollStartedAt } = options;

	const timedOut = polling && pollStartedAt !== undefined && Date.now() - pollStartedAt > MAX_POLL_MS;

	return useQuery({
		...withResponseValidation(codexStatusOptions()),
		// Poll while pending and not yet timed out; stop once we know the final state.
		refetchInterval: polling && !timedOut ? POLL_INTERVAL_MS : false,
		// Status endpoint is always fresh — never serve a stale cached value during polling.
		staleTime: 0,
	});
}

export function useCodexLogin(onAuthorizeUrl: (url: string) => void) {
	return useMutation({
		...withResponseValidation(codexLoginMutation()),
		onSuccess: (data) => {
			// authorizeUrl is typed optional in the generated schema; guard with fallback.
			onAuthorizeUrl(data.authorizeUrl ?? "");
		},
	});
}

export function useCodexLogout(onSuccess?: () => void) {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(codexLogoutMutation()),
		onSuccess: async (data) => {
			// The logout endpoint returns the post-logout status body (signedIn:false).
			// Write it directly into the cache so the UI reflects sign-out immediately
			// without waiting for the next status poll to complete.
			queryClient.setQueryData(codexStatusQueryKey(), data);
			await queryClient.invalidateQueries({ queryKey: codexStatusQueryKey() });
			onSuccess?.();
		},
		onError: (error) => {
			const message = apiErrorMessage(error, "Logout failed");
			toast.error(message);
		},
	});
}
