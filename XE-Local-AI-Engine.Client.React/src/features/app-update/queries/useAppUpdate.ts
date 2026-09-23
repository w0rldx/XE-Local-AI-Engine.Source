import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import type { ApplyAppUpdateData, GetAppUpdateStatusResponse } from "@/core/api/generated";
import { getAppUpdateStatus } from "@/core/api/generated/sdk.gen";
import {
	applyAppUpdateMutation,
	getAppUpdateStatusOptions,
	getAppUpdateStatusQueryKey,
	setAppUpdateChannelMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import type { Options } from "@/core/api/generated/sdk.gen";
import { callWithResponseValidation, withResponseValidation } from "@/core/api/ResponseValidation";

const emptyOptions = {} as Options<ApplyAppUpdateData>;

export function useAppUpdateStatus(refresh?: boolean) {
	return useQuery({
		...withResponseValidation(getAppUpdateStatusOptions({ query: { refresh: refresh ?? null } })),
		// Poll only the local cached snapshot. A live GitHub check remains explicit through refresh=true.
		refetchInterval: 60_000,
	});
}

export function useRefreshAppUpdateStatus() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: () => queryClient.fetchQuery(withResponseValidation(getAppUpdateStatusOptions({ query: { refresh: true } }))),
		onSuccess: (data) => {
			queryClient.setQueryData(getAppUpdateStatusQueryKey({ query: { refresh: null } }), data);
		},
	});
}

/** Reads the server's cached restart identity without replacing the status displayed by the mounted About dialog. */
export function useProbeAppUpdateStatus() {
	return useMutation({
		mutationFn: async () => {
			const { data } = await callWithResponseValidation(
				getAppUpdateStatus({
					query: { refresh: null },
					throwOnError: true,
				}),
			);
			return data;
		},
	});
}

export function useApplyAppUpdate() {
	const queryClient = useQueryClient();
	const statusKey = getAppUpdateStatusQueryKey({ query: { refresh: null } });
	return useMutation({
		...withResponseValidation(applyAppUpdateMutation()),
		onSuccess: (result) => {
			if (!result.applying) {
				queryClient.setQueryData<GetAppUpdateStatusResponse>(statusKey, (current) =>
					current ? { ...current, availableVersion: null, updateAvailable: false, checkStatus: "ready" } : current,
				);
			}
		},
	});
}

export function useSetAppUpdateChannel() {
	const queryClient = useQueryClient();
	const statusKey = getAppUpdateStatusQueryKey({ query: { refresh: null } });
	return useMutation({
		...withResponseValidation(setAppUpdateChannelMutation()),
		onSuccess: (fresh) => {
			// The endpoint answers with the status it just recomputed, so seeding the key the About dialog observes
			// renders the new channel on the next commit with no round-trip and no flicker.
			queryClient.setQueryData<GetAppUpdateStatusResponse>(statusKey, fresh);
			// Reconciles the 60 s poll, at the cost of one local GET. The `refresh: true` key differs in its second
			// element so prefix matching leaves it alone; nothing subscribes to it as a query. Not awaited and not
			// returned: `onSuccess` must not make `mutateAsync` wait for a refetch.
			queryClient.invalidateQueries({ queryKey: statusKey }).catch(() => undefined);
		},
	});
}

export { emptyOptions as noBodyOptions };
