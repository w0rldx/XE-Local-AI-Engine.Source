import type { QueryClient } from "@tanstack/react-query";

export interface MyRouterContext {
	queryClient: QueryClient;
}

export interface RootErrorComponentProps {
	error: unknown;
	reset: () => void;
}
