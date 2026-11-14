import { createFileRoute } from "@tanstack/react-router";

import { guardFirstRun } from "@/core/auth/utils/FirstRunGuard";
import { ExternalAccessSetup } from "@/features/node-settings/pages/ExternalAccessSetup";

export const Route = createFileRoute("/external-access")({
	beforeLoad: ({ context, location }) => guardFirstRun("/external-access", { location, queryClient: context.queryClient }),
	component: ExternalAccessSetup,
});
