import { createFileRoute } from "@tanstack/react-router";

import { DevelopmentPage } from "@/features/development/pages/DevelopmentPage";

export const Route = createFileRoute("/_layout/development")({
	component: DevelopmentPage,
});
