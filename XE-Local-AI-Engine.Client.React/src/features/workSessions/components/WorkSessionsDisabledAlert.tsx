import { Alert } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";

export interface WorkSessionsDisabledAlertProps {
	/** The capability query's error, if it has one. Present means the check FAILED, which is not the same as "off". */
	readonly error?: unknown;
}

/**
 * What this surface says when an operator has switched `WorkSessions:Enabled` off.
 *
 * Shared by the list page and the detail page because both are reachable on a disabled node (the detail one by a
 * bookmark or a pasted URL) and both would otherwise render the family's bodyless 404 as a generic "could not load".
 * Feature-local on purpose: the dev-workflow surface has its own copy with its own wording, and a shared
 * `<FeatureDisabled>` would have to take the strings as props, which is the same duplication with more indirection.
 *
 * A capability call that FAILED is rendered red with its own message rather than as "switched off": a check that did
 * not answer says nothing about the switch, and claiming otherwise would be a guess the operator cannot correct.
 */
export function WorkSessionsDisabledAlert({ error }: WorkSessionsDisabledAlertProps) {
	const { t } = useTranslation();

	return (
		<Alert color={error ? "red" : "yellow"} icon={<IconAlertTriangle size={16} />} data-testid="work-sessions-disabled">
			{error
				? apiErrorMessage(
						error,
						t("pages.workSessions.capabilityFailed", "Could not check whether work sessions are available on this node."),
					)
				: t("pages.workSessions.disabled", "Work sessions are disabled by this node's runtime configuration.")}
		</Alert>
	);
}
