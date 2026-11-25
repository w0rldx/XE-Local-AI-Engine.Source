import { HttpTransportType } from "@microsoft/signalr";

export const hubBearerProtocol = "xe.bearer";

// No SSE: EventSource can carry the token only in its URL. Not typed as IHttpConnectionOptions: its published
// typings omit the WebSocket option the library reads.
export const hubBearerTransportOptions =
	// Extending a missing WebSocket would throw at import, before negotiation could fall back to long polling.
	typeof WebSocket === "undefined"
		? { transport: HttpTransportType.LongPolling }
		: {
				transport: HttpTransportType.WebSockets | HttpTransportType.LongPolling,
				// Browsers cannot set Authorization on a WebSocket, so SignalR appends the token to the URL; move it into
				// the subprotocol list, which the node reads (NodeJwtBearerEvents) and which no console line prints.
				WebSocket: class HubBearerWebSocket extends WebSocket {
					constructor(url: string | URL, protocols?: string | string[]) {
						const target = new URL(url);
						const token = target.searchParams.get("access_token");
						target.searchParams.delete("access_token");
						// The constructor's SyntaxError quotes an invalid protocol, and SignalR logs that error at Error level.
						if (token && !/^[\w.-]+$/.test(token)) {
							throw new Error("The hub access token is not a valid WebSocket subprotocol.");
						}
						super(target, token ? [hubBearerProtocol, token] : (protocols ?? []));
					}
				},
			};
