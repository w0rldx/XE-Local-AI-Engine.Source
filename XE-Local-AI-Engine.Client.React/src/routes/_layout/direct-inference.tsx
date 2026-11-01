import { createFileRoute } from "@tanstack/react-router";

import { DirectInferencePage } from "@/features/directInference/pages/DirectInferencePage";

// Operator page over the chat hub, which is operator-authorized; the authenticated _layout is the gate, like /usage.
export const Route = createFileRoute("/_layout/direct-inference")({
	component: DirectInferencePage,
});
