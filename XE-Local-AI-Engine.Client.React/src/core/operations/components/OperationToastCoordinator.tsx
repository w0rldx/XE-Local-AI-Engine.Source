import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef } from "react";

import { useOperations } from "@/core/operations/hooks/useActiveOperations";
import { isOnOperationPage, markedTitle, showSystemNotification } from "@/core/operations/models/JobAlerts";
import { type OperationState, operationTransitions } from "@/core/operations/models/OperationModels";
import { useJobNotificationStore } from "@/core/operations/stores/JobNotificationStore";
import { llamaCppRuntimeInvalidationKey } from "@/core/runtime-acquisition/queries/useRuntimeAcquisitionStatus";
import { toast } from "@/core/ui/notifications/Toast";

// Headless, mounted once in the authenticated shell: it alerts once per operation that this page watched finish, and
// it is the one `watch` consumer of the feed, so it owns the job sources' hub leases. The state map lives in a ref and
// only an observed running-to-terminal step alerts, so a terminal state present at first sight (an operation that ended
// before the page opened, a reconnect re-hydration, a remount, a StrictMode re-run) stays silent. The toast id carries
// operation and state too, so a repeat call could only update the same toast. A failed download or runtime install also
// keeps its Activity row and the header indicator's error state; the toast is never its only trace.
//
// The alerts are for a user who is elsewhere: no toast while the operation's own page is open and visible, since it
// shows the outcome itself. While the tab is hidden each finish also counts into the tab title, cleared when the tab is
// seen again, and raises an OS notification when the user turned those on in this browser.
export function OperationToastCoordinator() {
	const operations = useOperations({ watch: true });
	const queryClient = useQueryClient();
	const seen = useRef<ReadonlyMap<string, OperationState>>(new Map());
	const unseen = useRef(0);

	useEffect(() => {
		const clearMarker = (): void => {
			if (unseen.current > 0 && document.visibilityState === "visible") {
				unseen.current = 0;
				document.title = markedTitle(document.title, 0);
			}
		};
		document.addEventListener("visibilitychange", clearMarker);
		return () => {
			document.removeEventListener("visibilitychange", clearMarker);
			// Signing out unmounts the shell; a count about the signed-in session must not outlive it.
			unseen.current = 0;
			document.title = markedTitle(document.title, 0);
		};
	}, []);

	useEffect(() => {
		const previous = seen.current;
		const { finished, seen: next } = operationTransitions(previous, operations);
		seen.current = next;
		const hidden = document.visibilityState === "hidden";
		for (const operation of finished) {
			if (!hidden && isOnOperationPage(operation, globalThis.location.pathname)) {
				continue;
			}
			const id = `operation:${operation.id}:${operation.state}`;
			if (operation.state === "succeeded") {
				toast.success(operation.label, { id });
			} else if (operation.detail === undefined) {
				toast.error(operation.label, { id });
			} else {
				toast.error(operation.detail, { id, title: operation.label });
			}
			if (hidden) {
				unseen.current += 1;
				document.title = markedTitle(document.title, unseen.current);
				if (useJobNotificationStore.getState().systemNotifications) {
					showSystemNotification(operation);
				}
			}
		}
		// Unlike the toast, this also fires on first sight: the runtime-status read (installed tag, update flag) may
		// predate the install, and nothing else refetches it, so the update banner would keep its pre-install answer.
		for (const operation of operations) {
			if (
				operation.kind === "runtime-acquisition" &&
				operation.state === "succeeded" &&
				previous.get(operation.id) !== "succeeded"
			) {
				queryClient.invalidateQueries({ queryKey: llamaCppRuntimeInvalidationKey }).catch(() => undefined);
			}
		}
	}, [operations, queryClient]);

	return null;
}
