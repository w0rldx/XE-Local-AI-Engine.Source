import { QueryClient } from "@tanstack/react-query";

const queryClient = new QueryClient({
	defaultOptions: {
		queries: {
			retry: (failureCount, error) => {
				const status = (error as { response?: { status?: number } }).response?.status;
				return status !== 401 && failureCount < 3;
			},
		},
	},
});

export function getContext() {
	return {
		queryClient,
	};
}

export { queryClient };
