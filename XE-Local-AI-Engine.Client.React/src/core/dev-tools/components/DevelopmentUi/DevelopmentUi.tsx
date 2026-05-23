import { TanStackDevtools } from "@tanstack/react-devtools";
import { FormDevtoolsPanel } from "@tanstack/react-form-devtools";
import { PacerDevtoolsPanel } from "@tanstack/react-pacer-devtools";
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
				{
					name: "TanStack Form",
					render: <FormDevtoolsPanel />,
					defaultOpen: false,
				},
				{
					name: "TanStack Pacer",
					render: <PacerDevtoolsPanel />,
					defaultOpen: false,
				},
			]}
		/>
	);
}
