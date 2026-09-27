import { createFileRoute } from "@tanstack/react-router";

import { AppUpdateChannelSelector } from "@/features/app-update/components/AppUpdateChannelSelector";
import { NodeSettings } from "@/features/node-settings/pages/NodeSettings";
import { nodeSettingsSearchSchema } from "@/features/node-settings/models/NodeSettingsSections";

// `?section=` makes each settings section linkable. The update-channel picker belongs to the app-update feature, so the
// route wires it in rather than the page importing across features.
function NodeSettingsRoute() {
	const { section } = Route.useSearch();
	const navigate = Route.useNavigate();
	return (
		<NodeSettings
			section={section}
			onSectionChange={(next) => {
				navigate({ search: { section: next } });
			}}
			updateChannelSelector={<AppUpdateChannelSelector />}
		/>
	);
}

export const Route = createFileRoute("/_layout/node-settings")({
	validateSearch: nodeSettingsSearchSchema,
	component: NodeSettingsRoute,
});
