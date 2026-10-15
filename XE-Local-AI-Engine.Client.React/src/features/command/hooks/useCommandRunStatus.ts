import { HubConnectionState } from "@microsoft/signalr";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";

import type { XeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowRunResponse as GraphWorkflowRunResponse } from "@/core/api/generated";
import { getGraphWorkflowRunOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";

const HUB_PATH = "graph-workflows/hub";
const terminalRunStatuses = new Set(["Completed", "Failed", "Cancelled"]);
/** Bounded fallback: only while the run hub is unavailable, and only until the run settles. */
const HUB_DOWN_POLL_MS = 5_000;

export function isTerminalRunStatus(status: string): boolean {
	return terminalRunStatuses.has(status);
}

/**
 * The live status of one run the command page started. The graph hub's pings carry no content, so each one for this run
 * re-reads the run; the intent response seeds the first paint.
 */
export function useCommandRunStatus(initial: GraphWorkflowRunResponse): string {
	const runId = initial.run.id;
	const queryClient = useQueryClient();
	const [hubDown, setHubDown] = useState(false);
	const query = useQuery({
		...withResponseValidation(getGraphWorkflowRunOptions({ path: { runId } })),
		initialData: initial,
		refetchInterval: (current) =>
			hubDown && !isTerminalRunStatus(current.state.data?.run.status ?? "") ? HUB_DOWN_POLL_MS : false,
	});

	useEffect(() => {
		const hub = acquireHubConnection(HUB_PATH);
		const { connection } = hub;
		const { queryKey } = getGraphWorkflowRunOptions({ path: { runId } });
		const refresh = (): void => {
			queryClient.invalidateQueries({ queryKey }).catch(() => undefined);
		};
		const onChanged = (change: { runId?: string } | null): void => {
			if (change?.runId === runId) {
				refresh();
			}
		};
		const subscribe = (): void => {
			if (connection.state !== HubConnectionState.Connected) {
				setHubDown(true);
				return;
			}
			connection
				.invoke("SubscribeRun", runId, 0)
				.then(() => {
					setHubDown(false);
					// Whatever moved between the intent answer and the subscription.
					refresh();
				})
				.catch(() => setHubDown(true));
		};
		connection.on("graphWorkflowChanged", onChanged);
		const removeReconnected = hub.onReconnected(subscribe);
		const removeClosed = hub.onClosed(() => setHubDown(true));
		hub.whenStarted.then(subscribe).catch(() => setHubDown(true));
		return () => {
			connection.off("graphWorkflowChanged", onChanged);
			removeReconnected();
			removeClosed();
			if (connection.state === HubConnectionState.Connected) {
				connection.invoke("UnsubscribeRun", runId).catch(() => undefined);
			}
			hub.release();
		};
	}, [queryClient, runId]);

	return query.data.run.status;
}
