import { createFileRoute } from "@tanstack/react-router";

import { Layout } from "@/core/layout/components/Layout/Layout";

export const Route = createFileRoute("/_layout")({
	component: (): React.ReactElement | null => <Layout />,
});
