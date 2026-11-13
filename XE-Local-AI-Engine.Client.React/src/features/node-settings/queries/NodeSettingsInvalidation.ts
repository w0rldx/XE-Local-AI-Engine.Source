import type { QueryClient } from "@tanstack/react-query";

import type { XeLocalAiEngineClientEndpointsNodeSettingsV1SaveNodeSettingsRequest as SaveNodeSettingsRequest } from "@/core/api/generated";
import {
	getDefaultAssistantToolOfferQueryKey,
	getDevelopmentCapabilityQueryKey,
	getGraphWorkflowCapabilityQueryKey,
	getNodeSettingsQueryKey,
	getToolCatalogQueryKey,
	getTranscriptionRuntimeStatusQueryKey,
	getWorkSessionCapabilityQueryKey,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { featureSwitchFields } from "@/features/node-settings/models/NodeSettingsFieldsModel";

export async function invalidateAfterNodeSettingsSave(queryClient: QueryClient, body: SaveNodeSettingsRequest): Promise<void> {
	await queryClient.invalidateQueries({ queryKey: getNodeSettingsQueryKey() });
	// The Default Assistant's tool offer and the tool catalog are computed from the live switches (web access,
	// tools, custom tools); a cached pre-save answer would show the old offer until it went stale. Every model
	// variant of the offer is matched by its operation id.
	await Promise.all(
		[getDefaultAssistantToolOfferQueryKey(), getToolCatalogQueryKey()].map(([{ _id }]) =>
			queryClient.invalidateQueries({ queryKey: [{ _id }] }),
		),
	);
	// The capability probes the nav and the gated pages read answer from the live switches; a cached answer from
	// before the save would keep a re-enabled feature hidden (or a disabled one offered) until it went stale. The
	// sandbox profile decides the capability's required and satisfied columns, so a profile change refreshes it too.
	if (featureSwitchFields.some((field) => field in body) || "sandboxSecurityProfile" in body) {
		await Promise.all(
			[
				getDevelopmentCapabilityQueryKey(),
				getGraphWorkflowCapabilityQueryKey(),
				getWorkSessionCapabilityQueryKey(),
				getTranscriptionRuntimeStatusQueryKey(),
			].map((queryKey) => queryClient.invalidateQueries({ queryKey })),
		);
	}
}
