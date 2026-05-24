import { createFileRoute } from "@tanstack/react-router";

import { RuntimeManager } from "@/features/runtime-manager/pages/RuntimeManager";

export const Route = createFileRoute("/_layout/manager")({
	component: RuntimeManager,
});
