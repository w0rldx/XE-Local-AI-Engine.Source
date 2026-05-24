import { createFileRoute } from "@tanstack/react-router";

import { Invocations } from "@/features/invocations/pages/Invocations";

export const Route = createFileRoute("/_layout/invocations")({
	component: Invocations,
});
