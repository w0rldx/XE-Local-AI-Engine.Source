import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	listNodeAuthSessionsOptions,
	listNodeAuthSessionsQueryKey,
	revokeAllNodeAuthSessionsMutation,
	revokeNodeAuthSessionMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";

// The operator's sign-in sessions. Revoking another session refetches the list; ending the CURRENT session (its own row
// or sign out everywhere) must not refetch, because this browser's token is already dead: the card clears auth instead.

// gcTime 0: the list and its `current` flag belong to one sign-in. Every sign-out leaves the page, so the entry is dropped
// with the card and a later sign-in never sees, or revokes from, the previous session's rows.
export function useNodeAuthSessions() {
	return useQuery({ ...listNodeAuthSessionsOptions(), gcTime: 0 });
}

export function useRevokeNodeAuthSession() {
	return useMutation(revokeNodeAuthSessionMutation());
}

export function useRevokeAllNodeAuthSessions() {
	return useMutation(revokeAllNodeAuthSessionsMutation());
}

export function useInvalidateNodeAuthSessions(): () => Promise<void> {
	const queryClient = useQueryClient();
	return () => queryClient.invalidateQueries({ queryKey: listNodeAuthSessionsQueryKey() });
}
