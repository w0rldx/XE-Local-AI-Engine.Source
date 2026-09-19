import { useQuery } from "@tanstack/react-query";

import { getNodeSettingsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import type { UiMode } from "@/capabilities/NodeCapabilities";

// Lives in core/layout rather than beside the other node-settings code because the two nav BARS are its
// primary callers and core may not import a feature (see .dependency-cruiser.cjs, no-core-to-features).
//
// The live navigation mode, read from the same node-settings query the layout guard already primed — so the nav bars
// resolve it from cache on the first paint rather than flashing one mode and then the other.
//
// Everything that is not exactly "simple" reads as "advanced": a node that never answered, an unreadable settings
// file, a failed request, a value from an older client. The failure direction is deliberate — the mode only hides
// entries, so guessing "advanced" shows too much, while guessing "simple" would hide pages from an operator who
// never asked for that.
export function useUiMode(): UiMode {
	const { data } = useQuery(getNodeSettingsOptions());
	return data?.uiMode === "simple" ? "simple" : "advanced";
}
