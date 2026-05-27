import { QueryClient } from "@tanstack/react-query";

const queryClient = new QueryClient();

export function getContext() {
	return {
		queryClient,
	};
}

export { queryClient };
