import { createFileRoute } from "@tanstack/react-router";

import { UsageDashboard } from "@/features/usage-dashboard/pages/UsageDashboard";

// Operator observability page. Like /invocations it consumes an operator-gated backend endpoint and carries no extra
// route guard: the authenticated _layout (node access token) IS the operator gate, and the endpoint 401s otherwise.
export const Route = createFileRoute("/_layout/usage")({
	component: UsageDashboard,
});
