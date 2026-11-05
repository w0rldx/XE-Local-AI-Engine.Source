import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef } from "react";

import { useOperations } from "@/core/operations/hooks/useActiveOperations";
import { type OperationState, operationTransitions } from "@/core/operations/models/OperationModels";
import { llamaCppRuntimeInvalidationKey } from "@/core/runtime-acquisition/queries/useRuntimeAcquisitionStatus";
import { toast } from "@/core/ui/notifications/Toast";

// Headless, mounted once in the authenticated shell: one toast per operation that this page watched finish. The state
// map lives in a ref and only an observed running-to-terminal step toasts, so a terminal state present at first sight
// (an operation that ended before the page opened, a reconnect re-hydration, a remount, a StrictMode re-run) stays
// silent. The toast id carries operation and state too, so a repeat call could only update the same toast. A failure
// also keeps its Activity row and the header indicator's error state; the toast is never its only trace.
export function OperationToastCoordinator() {
	const operations = useOperations();
	const queryClient = useQueryClient();
	const seen = useRef<ReadonlyMap<string, OperationState>>(new Map());

	useEffect(() => {
		const previous = seen.current;
		const { finished, seen: next } = operationTransitions(previous, operations);
		seen.current = next;
		for (const operation of finished) {
			const id = `operation:${operation.id}:${operation.state}`;
			if (operation.state === "succeeded") {
				toast.success(operation.label, { id });
			} else {
				toast.error(operation.detail ?? operation.label, { id, title: operation.label });
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
