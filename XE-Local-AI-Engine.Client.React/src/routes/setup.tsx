import { createFileRoute } from "@tanstack/react-router";

import { Setup } from "@/core/auth/pages/Setup";
import { guardFirstRun } from "@/core/auth/utils/FirstRunGuard";

export const Route = createFileRoute("/setup")({
	beforeLoad: ({ context, location }) => guardFirstRun("/setup", { location, queryClient: context.queryClient }),
	component: Setup,
});
