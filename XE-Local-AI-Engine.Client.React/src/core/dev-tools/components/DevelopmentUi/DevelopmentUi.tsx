import { TanStackDevtools } from "@tanstack/react-devtools";
import { ReactQueryDevtoolsPanel } from "@tanstack/react-query-devtools";
import { TanStackRouterDevtoolsPanel } from "@tanstack/react-router-devtools";

export function DevelopmentUi() {
	if (import.meta.env.PROD) {
		return null;
	}

	return (
		<TanStackDevtools
			config={{ hideUntilHover: true }}
			plugins={[
				{
					name: "TanStack Query",
					render: <ReactQueryDevtoolsPanel />,
					defaultOpen: true,
				},
				{
					name: "TanStack Router",
					render: <TanStackRouterDevtoolsPanel />,
					defaultOpen: false,
				},
			]}
		/>
	);
}
