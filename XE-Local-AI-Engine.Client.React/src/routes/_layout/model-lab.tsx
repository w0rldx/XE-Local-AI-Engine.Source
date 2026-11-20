import { createFileRoute } from "@tanstack/react-router";
import { useCallback } from "react";

import { type ModelLabTab, modelLabSearchSchema } from "@/features/modelLab/models/ModelLabNavigation";
import { ModelLabPage } from "@/features/modelLab/pages/ModelLabPage";

// Operator page over the chat hub and the models API; the authenticated _layout is the gate, like /direct-inference.
export const Route = createFileRoute("/_layout/model-lab")({
	validateSearch: modelLabSearchSchema,
	component: ModelLabRoute,
});

// Thin router adapter: ModelLabPage stays router-free (it is rendered directly in unit tests), so `?tab=` is read here
// and handed down as props.
function ModelLabRoute() {
	const { tab } = Route.useSearch();
	const navigate = Route.useNavigate();

	const handleTabChange = useCallback(
		(next: ModelLabTab) => {
			// `replace` so switching tabs does not push a history entry per click — Back should leave the page. Best-effort:
			// a failed search-param update must not break the page.
			navigate({ search: { tab: next }, replace: true }).catch(() => undefined);
		},
		[navigate],
	);

	return <ModelLabPage tab={tab} onTabChange={handleTabChange} />;
}
