import { createFileRoute } from "@tanstack/react-router";

import { VaultSetup } from "@/core/auth/pages/VaultSetup";
import { guardFirstRun } from "@/core/auth/utils/FirstRunGuard";

// Open only to a signed-in operator of a node whose vault is still pending; every other state has a better place to be.
export const Route = createFileRoute("/vault-setup")({
	beforeLoad: ({ context, location }) => guardFirstRun("/vault-setup", { location, queryClient: context.queryClient }),
	component: VaultSetup,
});
