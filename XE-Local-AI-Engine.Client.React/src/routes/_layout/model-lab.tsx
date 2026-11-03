import { createFileRoute } from "@tanstack/react-router";

import { ModelLabPage } from "@/features/modelLab/pages/ModelLabPage";

// Operator page over the chat hub and the models API; the authenticated _layout is the gate, like /direct-inference.
export const Route = createFileRoute("/_layout/model-lab")({
	component: ModelLabPage,
});
