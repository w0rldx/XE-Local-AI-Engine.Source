import { useDisclosure } from "@mantine/hooks";
import { useNavigate } from "@tanstack/react-router";
import { lazy, Suspense } from "react";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { Layout } from "@/core/layout/components/Layout/Layout";
import { ActiveOperationsIndicator } from "@/core/operations/components/ActiveOperationsIndicator";
import { OperationToastCoordinator } from "@/core/operations/components/OperationToastCoordinator";
import { AboutDialogButton } from "@/features/about/components/AboutDialogButton/AboutDialogButton";
import { ChatConnectionStatusChip } from "@/features/chat/components/ChatConnectionStatusChip";
import { ReportProblemButton } from "@/features/diagnostics/components/ReportProblemButton";
import { useReportProblem } from "@/features/diagnostics/hooks/useReportProblem";
import { CpuFallbackBanner } from "@/features/model-fit/components/CpuFallbackBanner";
import { LlamaCppUpdateBanner } from "@/features/node-settings/components/LlamaCppUpdateBanner";
import { AiAcknowledgementGate } from "@/features/onboarding/components/AiAcknowledgementGate";
import { PendingRestartBanner } from "@/features/node-settings/components/PendingRestartBanner";
import { RuntimeResourcesWidget } from "@/features/runtime-resources/components/RuntimeResourcesWidget";

// Same lazy pattern as AboutDialogButton: the dialog bundle only loads on first open.
const AboutDialog = lazy(async () => {
	const module = await import("@/features/about/components/AboutDialog/AboutDialog");
	return { default: module.AboutDialog };
});

/** The authenticated app shell: core's Layout frame with the feature widgets that live in it on every page. */
export function AppLayout() {
	const navigate = useNavigate();
	const openDiagnostics = () => {
		navigate({ to: "/diagnostics" }).catch(() => undefined);
	};
	const { report, pending } = useReportProblem(openDiagnostics);
	const [aboutOpened, { open: openAbout, close: closeAbout }] = useDisclosure(false);

	return (
		<Layout
			// Running and failed operations (the first-run runtime download among them, which starts at host boot while
			// the user can be anywhere), on every page and screen size; Home's Activity card holds the detail.
			headerIndicator={<ActiveOperationsIndicator />}
			headerStatus={<RuntimeResourcesWidget />}
			headerActions={
				<>
					<ReportProblemButton onReported={openDiagnostics} />
					<AboutDialogButton />
				</>
			}
			drawerActions={{ reportProblem: report, reportProblemPending: pending, openAbout }}
			banners={
				<>
					{nodeCapabilities.modelFit ? <CpuFallbackBanner /> : null}
					<LlamaCppUpdateBanner />
					{/* Saved settings the running node applies only after a restart; shown on every page until then. */}
					<PendingRestartBanner />
				</>
			}
			overlay={
				<>
					{/* The one-time AI notice opens on the first authenticated page, whichever it is: one mount, in the shell. */}
					<AiAcknowledgementGate />
					<OperationToastCoordinator />
					<ChatConnectionStatusChip />
					{/* Outside the mobile drawer so the dialog survives the drawer closing when About is opened. */}
					{aboutOpened ? (
						<Suspense fallback={null}>
							<AboutDialog opened={aboutOpened} onClose={closeAbout} />
						</Suspense>
					) : null}
				</>
			}
		/>
	);
}
