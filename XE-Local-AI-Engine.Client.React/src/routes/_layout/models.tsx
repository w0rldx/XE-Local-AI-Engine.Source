import { createFileRoute } from "@tanstack/react-router";

import { ModelManagement } from "@/features/models/pages/ModelManagement";

export const Route = createFileRoute("/_layout/models")({
	component: ModelManagement,
});
