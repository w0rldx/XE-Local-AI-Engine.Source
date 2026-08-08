import type { XeLocalAiEngineClientEndpointsConnectionV1ConnectionStatusResponse } from "@/core/api/generated";
import type { ConnectionStatusViewModel } from "@/features/dashboard/models/ConnectionStatusModel";

// Maps the generated (OpenAPI) connection-status response to the stricter domain view-model the dashboard depends
// on. The generated type is the single source of truth for the wire shape; its fields are all optional (`x?: T`),
// so each field coalesces to a required value with a sensible default.
export function toConnectionStatusViewModel(
	dto: XeLocalAiEngineClientEndpointsConnectionV1ConnectionStatusResponse,
): ConnectionStatusViewModel {
	return {
		state: dto.state ?? "unknown",
		lastError: dto.lastError ?? null,
		lastUpdatedAt: dto.lastUpdatedAt ?? "",
		isPaired: dto.isPaired ?? false,
		autoConnectOnStart: dto.autoConnectOnStart ?? false,
		bindingMethod: dto.bindingMethod ?? null,
		lastKnownNodeName: dto.lastKnownNodeName ?? null,
		tokenExpiresAt: dto.tokenExpiresAt ?? null,
		canConnect: dto.canConnect ?? false,
		canDisconnect: dto.canDisconnect ?? false,
		canEnableAutoConnect: dto.canEnableAutoConnect ?? false,
		canDisableAutoConnect: dto.canDisableAutoConnect ?? false,
	};
}
