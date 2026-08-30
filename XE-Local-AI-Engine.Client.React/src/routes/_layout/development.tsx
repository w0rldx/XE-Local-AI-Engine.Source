import { createFileRoute } from "@tanstack/react-router";
import { z } from "zod";

import { DevelopmentConsentGate } from "@/features/development/components/DevelopmentConsentGate";
import { DevelopmentPage } from "@/features/development/pages/DevelopmentPage";

// Deep-link targets, both optional (X8): a workflow's DevTask node links here with the project and task it drove, so
// the operator lands on that project's evidence rather than on whichever project happens to be first. Arriving without
// them is unchanged — the page still defaults to the first project.
const developmentSearchSchema = z.object({
	project: z.string().optional(),
	task: z.string().optional(),
});

// The consent disclosure gates the route rather than sitting inside the page: it has to be impossible to start an
// attempt behind an unacknowledged notice, and keeping it here leaves the page's own tests free of the gate.
function DevelopmentRoute() {
	const { project, task } = Route.useSearch();
	return (
		<DevelopmentConsentGate>
			<DevelopmentPage initialProjectId={project} initialTaskId={task} />
		</DevelopmentConsentGate>
	);
}

export const Route = createFileRoute("/_layout/development")({
	validateSearch: developmentSearchSchema,
	component: DevelopmentRoute,
});
