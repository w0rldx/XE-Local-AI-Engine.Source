import { createFileRoute } from "@tanstack/react-router";

import { DiagnosticsPanel } from "@/features/diagnostics/components/DiagnosticsPanel";

export const Route = createFileRoute("/_layout/diagnostics")({
	component: DiagnosticsPanel,
});
