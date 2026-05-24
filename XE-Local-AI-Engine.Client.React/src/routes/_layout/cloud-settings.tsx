import { createFileRoute } from "@tanstack/react-router";

import { CloudSettings } from "@/features/cloud-settings/pages/CloudSettings";

export const Route = createFileRoute("/_layout/cloud-settings")({
	component: CloudSettings,
});
