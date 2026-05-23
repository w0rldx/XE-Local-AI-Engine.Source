import { QueryClient, QueryClientProvider } from "@tanstack/react-query";

const queryClient = new QueryClient();

// biome-ignore lint/style/useComponentExportOnlyModules: <This is a singleton for the query client>
export function getContext() {
	return {
		queryClient,
	};
}

export function Provider({ children }: { children: React.ReactNode }) {
	return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}
