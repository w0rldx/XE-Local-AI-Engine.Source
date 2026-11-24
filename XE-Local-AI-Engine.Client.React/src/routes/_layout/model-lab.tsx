import { createFileRoute } from "@tanstack/react-router";

import { modelLabSearchSchema } from "@/features/modelLab/models/ModelLabNavigation";
import { ModelLabRoutePage } from "@/features/modelLab/pages/ModelLabRoutePage";

// Operator page over the chat hub and the models API; the authenticated _layout is the gate, like /direct-inference.
export const Route = createFileRoute("/_layout/model-lab")({
	validateSearch: modelLabSearchSchema,
	component: ModelLabRoutePage,
});
