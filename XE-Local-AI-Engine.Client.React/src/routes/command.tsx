import { createFileRoute } from "@tanstack/react-router";

import { ensureAuthenticated } from "@/core/auth/utils/EnsureAuthenticated";
import { CommandPage } from "@/features/command/pages/CommandPage";

// Outside `/_layout` on purpose: the desktop shell's overlay window shows this page at 520×220, where the sidebar and
// header would leave no room for it. The sign-in chain is the same one `/_layout` runs; a browser tab reaches the page
// from the navigation and leaves it through the page's own home button.
export const Route = createFileRoute("/command")({
	beforeLoad: ({ location }) => ensureAuthenticated(location),
	component: CommandPage,
});
