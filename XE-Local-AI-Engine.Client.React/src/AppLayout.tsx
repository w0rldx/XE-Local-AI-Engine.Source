import { useDisclosure } from "@mantine/hooks";
import { useNavigate } from "@tanstack/react-router";
import { lazy, Suspense } from "react";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { Layout } from "@/core/layout/components/Layout/Layout";
import { AboutDialogButton } from "@/features/about/components/AboutDialogButton/AboutDialogButton";
import { ChatConnectionStatusChip } from "@/features/chat/components/ChatConnectionStatusChip";
import { ReportProblemButton } from "@/features/diagnostics/components/ReportProblemButton";
import { useReportProblem } from "@/features/diagnostics/hooks/useReportProblem";
import { CpuFallbackBanner } from "@/features/model-fit/components/CpuFallbackBanner";
import { LlamaCppUpdateBanner } from "@/features/node-settings/components/LlamaCppUpdateBanner";
import { PendingRestartBanner } from "@/features/node-settings/components/PendingRestartBanner";
import { RuntimeAcquisitionBanner } from "@/features/node-settings/components/RuntimeAcquisitionBanner";
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
					{/* First-run runtime acquisition. Mounted here, not on a page, because the download starts at host boot
					    and the user can be anywhere in the app while it runs. */}
					<RuntimeAcquisitionBanner />
					{/* Saved settings the running node applies only after a restart; shown on every page until then. */}
					<PendingRestartBanner />
				</>
			}
			overlay={
				<>
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
