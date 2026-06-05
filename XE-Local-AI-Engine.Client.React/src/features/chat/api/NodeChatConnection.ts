import { type HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";

import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { refreshNodeAuthToken } from "@/core/auth/api/NodeAuthApi";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

/* eslint-disable react-doctor/async-await-in-loop */

const chatHubPath = "chat/hub";

// SignalR's withAutomaticReconnect retries dropped connections on this schedule but
// does NOT retry the initial start() — that is handled by initialStartDelaysMs below.
const reconnectDelaysMs = [0, 2_000, 5_000, 10_000, 30_000];
const initialStartDelaysMs = [0, 1_000, 2_000, 5_000, 10_000];

// Renew the access token a little before it expires so the SignalR HTTP negotiate /
// WebSocket upgrade never carries a token that is about to lapse.
const tokenRenewSkewMs = 30_000;

export type NodeChatConnectionStatus = "disconnected" | "connecting" | "connected" | "reconnecting";

export interface NodeChatConnectionEvents {
	onStatusChange?: (status: NodeChatConnectionStatus) => void;
	onReconnected?: (connectionId: string | undefined) => void;
	onReconnecting?: (error: Error | undefined) => void;
	onClose?: (error: Error | undefined) => void;
}

function delay(ms: number): Promise<void> {
	return new Promise((resolve) => setTimeout(resolve, ms));
}

function isExpired(expiresAtUtc: string | undefined): boolean {
	if (!expiresAtUtc) {
		return true;
	}
	const expiresAt = Date.parse(expiresAtUtc);
	if (Number.isNaN(expiresAt)) {
		return true;
	}
	return expiresAt - Date.now() <= tokenRenewSkewMs;
}

// accessTokenFactory runs before each negotiate/transport HTTP request. Renew the token
// when it is missing or near expiry so a long-lived connection keeps authenticating
// across reconnects. Never log the returned token — it may end up in WS/SSE query strings.
async function resolveAccessToken(): Promise<string> {
	const state = useNodeAuthStore.getState();
	if (state.accessToken && !isExpired(state.expiresAtUtc)) {
		return state.accessToken;
	}

	try {
		const token = await refreshNodeAuthToken();
		useNodeAuthStore.getState().actions.setToken(token);
		return token.accessToken;
	} catch {
		// Fall back to whatever token we hold; the hub will reject if it is invalid.
		return useNodeAuthStore.getState().accessToken ?? "";
	}
}

/**
 * Module-level long-lived SignalR connection to the local chat hub. A single connection is
 * shared across every send so reconnect/resume state survives individual stream lifetimes.
 */
class NodeChatConnectionManager {
	private connection: HubConnection | undefined;
	private startPromise: Promise<void> | undefined;
	private listeners = new Set<NodeChatConnectionEvents>();

	subscribe(events: NodeChatConnectionEvents): () => void {
		this.listeners.add(events);
		return () => {
			this.listeners.delete(events);
		};
	}

	get status(): NodeChatConnectionStatus {
		return this.toStatus(this.connection?.state);
	}

	get connectionId(): string | undefined {
		return this.connection?.connectionId ?? undefined;
	}

	/**
	 * Returns the live connection for streaming, or undefined when none is connected yet. Callers that need a
	 * guaranteed-started connection should await {@link ensureConnection} instead.
	 */
	current(): HubConnection | undefined {
		return this.connection && this.connection.state === HubConnectionState.Connected ? this.connection : undefined;
	}

	/**
	 * Returns a started connection, building it on first use and reusing it afterwards.
	 * Retries the initial start() with backoff because withAutomaticReconnect does not.
	 */
	async ensureConnection(): Promise<HubConnection> {
		const connection = this.getOrBuildConnection();
		if (connection.state === HubConnectionState.Connected) {
			return connection;
		}

		if (!this.startPromise) {
			this.startPromise = this.startWithRetry(connection).finally(() => {
				this.startPromise = undefined;
			});
		}

		await this.startPromise;
		return connection;
	}

	private getOrBuildConnection(): HubConnection {
		if (this.connection) {
			return this.connection;
		}

		const connection = new HubConnectionBuilder()
			.withUrl(buildLocalApiUrl(chatHubPath), {
				accessTokenFactory: () => resolveAccessToken(),
			})
			.withAutomaticReconnect(reconnectDelaysMs)
			.configureLogging(LogLevel.Warning)
			.build();

		connection.onreconnecting((error) => {
			this.emitStatus("reconnecting");
			for (const listener of this.listeners) {
				listener.onReconnecting?.(error);
			}
		});
		connection.onreconnected((connectionId) => {
			this.emitStatus("connected");
			for (const listener of this.listeners) {
				listener.onReconnected?.(connectionId ?? undefined);
			}
		});
		connection.onclose((error) => {
			this.emitStatus("disconnected");
			for (const listener of this.listeners) {
				listener.onClose?.(error);
			}
		});

		this.connection = connection;
		return connection;
	}

	private async startWithRetry(connection: HubConnection): Promise<void> {
		let lastError: unknown;
		for (let attempt = 0; attempt < initialStartDelaysMs.length; attempt += 1) {
			if (connection.state === HubConnectionState.Connected) {
				return;
			}
			const backoffMs = initialStartDelaysMs[attempt] ?? 0;
			if (backoffMs > 0) {
				// biome-ignore lint/performance/noAwaitInLoops: initial-start retries must back off sequentially before the next start() attempt.
				await delay(backoffMs);
			}

			this.emitStatus("connecting");
			try {
				await connection.start();
				this.emitStatus("connected");
				return;
			} catch (error) {
				lastError = error;
			}
		}

		this.emitStatus("disconnected");
		throw lastError ?? new Error("Unable to start the local chat connection.");
	}

	private toStatus(state: HubConnectionState | undefined): NodeChatConnectionStatus {
		switch (state) {
			case HubConnectionState.Connected:
				return "connected";
			case HubConnectionState.Connecting:
				return "connecting";
			case HubConnectionState.Reconnecting:
				return "reconnecting";
			default:
				return "disconnected";
		}
	}

	private emitStatus(status: NodeChatConnectionStatus): void {
		for (const listener of this.listeners) {
			listener.onStatusChange?.(status);
		}
	}
}

export const nodeChatConnection = new NodeChatConnectionManager();
