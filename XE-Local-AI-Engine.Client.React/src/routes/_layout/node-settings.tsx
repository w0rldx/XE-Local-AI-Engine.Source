import { useQuery } from "@tanstack/react-query";
import { createFileRoute } from "@tanstack/react-router";

import { getDevelopmentCapabilityOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { AppUpdateChannelSelector } from "@/features/app-update/components/AppUpdateChannelSelector";
import { SandboxIsolationPanel } from "@/features/development/components/SandboxIsolationPanel";
import { NodeSettings } from "@/features/node-settings/pages/NodeSettings";
import { nodeSettingsSearchSchema } from "@/features/node-settings/models/NodeSettingsSections";

// `?section=` makes each settings section linkable. The update-channel picker and the isolation table belong to other
// features, so the route wires them in rather than the page importing across features. The capability behind the
// table is read only while the Sandbox section is open; it shares the cache entry the page reads and invalidates.
function NodeSettingsRoute() {
	const { section } = Route.useSearch();
	const navigate = Route.useNavigate();
	const capability = useQuery({
		...withResponseValidation(getDevelopmentCapabilityOptions()),
		enabled: section === "sandbox",
	});
	return (
		<NodeSettings
			section={section}
			onSectionChange={(next) => {
				navigate({ search: { section: next } });
			}}
			updateChannelSelector={<AppUpdateChannelSelector />}
			sandboxIsolationPanel={<SandboxIsolationPanel roles={capability.data?.isolation} />}
		/>
	);
}

export const Route = createFileRoute("/_layout/node-settings")({
	validateSearch: nodeSettingsSearchSchema,
	component: NodeSettingsRoute,
});
