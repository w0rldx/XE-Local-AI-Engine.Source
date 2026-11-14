import { type HubConnection, HubConnectionState } from "@microsoft/signalr";

import type { SharedHubHandle } from "@/core/api/signalr/SharedHubConnection";

// Server group membership on a SHARED connection, refcounted per (connection, leave call). SignalR groups belong to the
// connection, not to the hook that joined, so two consumers of one shared connection (a page and the app-wide
// operations feed, say) hold the same membership: one consumer's Unsubscribe would silently drop the other's.

/** The hub method that leaves the group and its arguments, e.g. `["Unsubscribe", runId]`; also the group's identity. */
export type HubGroupLeaveCall = readonly [method: string, ...args: unknown[]];

const holdersByConnection = new WeakMap<HubConnection, Map<string, number>>();

/**
 * Holds one server group on `hub`'s shared connection for one consumer and returns the leave function (call it before
 * `hub.release()`). `join` is the consumer's own Subscribe call, so each consumer still gets its own replay: it runs once
 * the start settles (`reconnected` false) and after every reconnect or restart (true), after the handle's earlier
 * `onReconnected` callbacks. `leaveCall` is sent only when the last holder of the same call on that connection leaves,
 * and only while connected (a dropped connection holds no membership to leave). `join` checks the state itself.
 */
export function joinHubGroup(
	hub: SharedHubHandle,
	leaveCall: HubGroupLeaveCall,
	join: (reconnected: boolean) => void,
): () => void {
	const { connection } = hub;
	let groups = holdersByConnection.get(connection);
	if (groups === undefined) {
		groups = new Map();
		holdersByConnection.set(connection, groups);
	}
	const heldGroups = groups;
	const key = JSON.stringify(leaveCall);
	heldGroups.set(key, (heldGroups.get(key) ?? 0) + 1);

	let held = true;
	// A rejected start (the manager swallows its own, so only a stand-in rejects) joins nothing; a consumer that degrades
	// on it keeps its own `whenStarted.catch`.
	hub.whenStarted
		.then(() => {
			if (held) {
				join(false);
			}
		})
		.catch(() => undefined);
	const removeReconnected = hub.onReconnected(() => {
		if (held) {
			join(true);
		}
	});

	return () => {
		if (!held) {
			return;
		}
		held = false;
		removeReconnected();
		const remaining = (heldGroups.get(key) ?? 1) - 1;
		if (remaining > 0) {
			heldGroups.set(key, remaining);
			return;
		}
		heldGroups.delete(key);
		if (connection.state === HubConnectionState.Connected) {
			const [method, ...args] = leaveCall;
			connection.invoke(method, ...args).catch(() => undefined);
		}
	};
}
