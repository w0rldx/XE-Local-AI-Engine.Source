import { createFileRoute } from "@tanstack/react-router";

import { guardFirstRun } from "@/core/auth/utils/FirstRunGuard";

// A retired first-run screen (the interface mode now defaults and is offered after the first answer): an old bookmark
// goes on to the step the node still owes, or the app.
export const Route = createFileRoute("/ui-mode-setup")({
	beforeLoad: ({ context, location }) => guardFirstRun("retired", { location, queryClient: context.queryClient }),
});
