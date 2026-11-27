import { type HubConnection, HubConnectionBuilder, LogLevel } from "@microsoft/signalr";

import { hubBearerTransportOptions } from "@/core/api/signalr/HubBearerTransport";
import { resolveHubAccessToken } from "@/core/api/signalr/ResolveHubAccessToken";

/**
 * Builds every node hub connection, so none can skip the token-safe transport or the log floor. Omitted
 * `reconnectDelaysMs` keeps SignalR's default reconnect schedule.
 */
export function buildHubConnection(
	url: string,
	options: { headers?: Record<string, string>; reconnectDelaysMs?: number[] } = {},
): HubConnection {
	const builder = new HubConnectionBuilder()
		.withUrl(url, {
			accessTokenFactory: () => resolveHubAccessToken(),
			headers: options.headers,
			...hubBearerTransportOptions,
		})
		// Never below Warning: SignalR logs the socket URL, token included, at Information.
		.configureLogging(LogLevel.Warning);
	return (
		options.reconnectDelaysMs ? builder.withAutomaticReconnect(options.reconnectDelaysMs) : builder.withAutomaticReconnect()
	).build();
}
