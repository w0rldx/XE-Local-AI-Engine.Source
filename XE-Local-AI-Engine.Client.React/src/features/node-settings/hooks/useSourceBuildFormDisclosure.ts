import { useDisclosure } from "@mantine/hooks";
import { useState } from "react";

/**
 * Open/closed state for the build form, shared so the three cards cannot drift apart.
 *
 * @param needsAttention Derived from status data ALONE — a build running, a build that ended in a failure, or an
 *   invalid managed record. Never from a probe: deciding whether to show the form must not cost the probe that the
 *   form exists to defer.
 */
export function useSourceBuildFormDisclosure(needsAttention: boolean): { opened: boolean; toggle: () => void } {
	const [opened, { toggle, open }] = useDisclosure(false);
	// A one-way latch. Auto-expansion fires on the first render that reports the condition and never again, so the
	// status tick where a build stops running cannot collapse the form under the operator who is reading it — and an
	// operator who deliberately closed it does not have it reopened on the next poll either.
	const [autoExpanded, setAutoExpanded] = useState(false);
	if (needsAttention && !autoExpanded) {
		setAutoExpanded(true);
		open();
	}

	return { opened, toggle };
}
