export type ConnectionStateValue = "disconnected" | "connecting" | "connected" | "reconnecting" | "pairing" | "error" | "preparing-model" | "unknown";

// Stricter domain view-model the dashboard renders. The generated connection-status response has all-optional
// fields (`x?: T`); this shape coalesces every field to a required value so the page never null-checks the wire
// shape. Produced by ConnectionMappers.toConnectionStatusViewModel.
export interface ConnectionStatusViewModel {
	state: string;
	lastError: string | null;
	lastUpdatedAt: string;
	isPaired: boolean;
	autoConnectOnStart: boolean;
	bindingMethod: string | null;
	lastKnownNodeName: string | null;
	tokenExpiresAt: string | null;
	canConnect: boolean;
	canDisconnect: boolean;
	canEnableAutoConnect: boolean;
	canDisableAutoConnect: boolean;
}

export function connectionStatusColor(state: string): "blue" | "green" | "orange" | "red" | "gray" {
	switch (state) {
		case "connected":
			return "green";
		case "connecting":
		case "reconnecting":
		case "preparing-model":
		case "pairing":
			return "blue";
		case "error":
			return "red";
		case "disconnected":
			return "gray";
		default:
			return "orange";
	}
}

export function connectionStatusLabel(state: string): string {
	switch (state) {
		case "preparing-model":
			return "Preparing model";
		case "reconnecting":
			return "Reconnecting";
		case "connecting":
			return "Connecting";
		case "connected":
			return "Connected";
		case "disconnected":
			return "Disconnected";
		case "pairing":
			return "Pairing";
		case "error":
			return "Error";
		default:
			return "Unknown";
	}
}

export function formatOptionalDate(value?: string | null): string {
	if (!value) {
		return "Not available";
	}

	const date = new Date(value);
	return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

export function connectionActionHint(state: string, autoConnectOnStart: boolean): string {
	if (state === "reconnecting") {
		return "Disable auto-connect to stop reconnect attempts, or wait for the worker to recover.";
	}

	if (autoConnectOnStart) {
		return "Auto-connect is enabled. The worker will connect on startup after prerequisites are ready.";
	}

	return "Auto-connect is disabled. Use Connect for a manual session or enable startup connection.";
}
