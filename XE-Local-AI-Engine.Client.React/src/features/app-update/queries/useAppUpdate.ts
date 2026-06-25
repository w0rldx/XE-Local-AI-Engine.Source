import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import type {
	ApplyAppUpdateData,
	PollGitHubAuthData,
	SignOutGitHubAuthData,
	StartGitHubAuthData,
} from "@/core/api/generated";
import {
	applyAppUpdateMutation,
	getAppUpdateStatusOptions,
	getAppUpdateStatusQueryKey,
	getGitHubAuthStatusOptions,
	getGitHubAuthStatusQueryKey,
	pollGitHubAuthMutation,
	signOutGitHubAuthMutation,
	startGitHubAuthMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import type { Options } from "@/core/api/generated/sdk.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

// Auth state literals as returned by the backend.
export type AuthState = "signedIn" | "signedOut" | "reauthRequired" | "noAccess";

// Query IDs used for cache invalidation.
const queryIds = {
	appUpdateStatus: "getAppUpdateStatus",
	gitHubAuthStatus: "getGitHubAuthStatus",
} as const;

// Invalidation helper shared by mutations that change auth/update state.
function invalidateStatus(queryClient: ReturnType<typeof useQueryClient>): Promise<void[]> {
	return Promise.all([
		queryClient.invalidateQueries({
			// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
			queryKey: [{ _id: queryIds.appUpdateStatus }],
		}),
		queryClient.invalidateQueries({
			// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
			queryKey: [{ _id: queryIds.gitHubAuthStatus }],
		}),
	]);
}

// No-body request object for endpoints that take no body/path/query (just the url).
// All three fields are optional/never in the generated data types so `{}` satisfies them.
const emptyOptions = {} as Options<StartGitHubAuthData & PollGitHubAuthData & SignOutGitHubAuthData & ApplyAppUpdateData>;

// Combined app-update + auth status. Pass refresh:true to force a server-side check (60s floor enforced by backend).
export function useAppUpdateStatus(refresh?: boolean) {
	return useQuery({
		...withResponseValidation(getAppUpdateStatusOptions({ query: { refresh: refresh ?? null } })),
	});
}

// Forces a live server-side check (?refresh=true, 60s floor enforced by backend) and writes the fresh snapshot back
// into the default `useAppUpdateStatus()` cache so the displayed status updates. Returns the refresh callback plus an
// `isRefreshing` flag for button loading state. The default query (refresh:null) only re-serves the cached snapshot on
// `refetch()`, so this is what the "Check for updates" button must call to actually hit GitHub.
export function useRefreshAppUpdateStatus() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: () =>
			queryClient.fetchQuery(
				withResponseValidation(getAppUpdateStatusOptions({ query: { refresh: true } })),
			),
		onSuccess: (data) => {
			// Seed the default (refresh:null) cache so the visible status reflects the forced check.
			queryClient.setQueryData(
				getAppUpdateStatusQueryKey({ query: { refresh: null } }),
				data,
			);
		},
	});
}

// GitHub auth status (signed-in / signed-out / reauthRequired / noAccess).
export function useGitHubAuthStatus() {
	return useQuery({
		...withResponseValidation(getGitHubAuthStatusOptions()),
	});
}

// Starts the GitHub device flow. Returns userCode / verificationUri / expiresInSeconds / intervalSeconds.
// The device_code is intentionally held only by the backend; React never sees it.
export function useStartGitHubAuth() {
	return useMutation({
		...withResponseValidation(startGitHubAuthMutation()),
	});
}

// Polls the device flow until authorized / denied / expired. Caller drives the poll interval.
export function usePollGitHubAuth() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(pollGitHubAuthMutation()),
		onSuccess: async (data) => {
			if (data?.state === "authorized") {
				await invalidateStatus(queryClient);
			}
		},
	});
}

// Clears the local stored token. Attempts a best-effort server-side revoke call, but this is
// effectively a no-op: the GitHub device flow has no client_secret, so GitHub cannot verify the
// revoke request and will not actually invalidate the token server-side.
export function useSignOutGitHubAuth() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(signOutGitHubAuthMutation()),
		onSuccess: () => invalidateStatus(queryClient),
	});
}

// Triggers download + apply + relaunch. No-op if no update is available.
export function useApplyAppUpdate() {
	return useMutation({
		...withResponseValidation(applyAppUpdateMutation()),
	});
}

// Convenience empty-options export for callers that invoke no-body mutations.
// Use as: `mutation.mutate(noBodyOptions)` or `mutation.mutateAsync(noBodyOptions)`.
export { emptyOptions as noBodyOptions };
export { getAppUpdateStatusQueryKey, getGitHubAuthStatusQueryKey };
