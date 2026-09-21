import { createFileRoute } from "@tanstack/react-router";

import { AgentRunsPage } from "@/features/agentRuns/pages/AgentRunsPage";

// Operator forensics page. Like /invocations and /usage it consumes an operator-gated backend endpoint and carries
// no extra route guard: the authenticated _layout (node access token) IS the operator gate, and the endpoint 401s
// otherwise. It has no capability flag of its own — AgentHome ships in every build, and the node's own
// `AgentHome:Enabled` decides whether runs are ever written, which this page reports honestly as an empty history.
export const Route = createFileRoute("/_layout/agent-runs")({
	component: AgentRunsPage,
});
