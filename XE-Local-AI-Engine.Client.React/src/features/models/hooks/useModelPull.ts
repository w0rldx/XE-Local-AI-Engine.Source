import { useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { listLocalModelsQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { toast } from "@/core/ui/notifications/Toast";
import { streamModelPull } from "@/features/models/api/ModelPullStream";

// Minimum gap (ms) between progress UI updates while the status is unchanged. Ollama emits many "downloading" lines
// per second; pushing each one into React state + a Mantine toast update caused a re-render storm that froze the UI
// during a large pull. We coalesce same-status byte updates to at most one per second (a phase change — e.g.
// "downloading" -> "verifying" -> "success" — always renders immediately, so the bar stays responsive without flooding).
const PROGRESS_THROTTLE_MS = 1000;

// Derives a 0–100 percent from sanitized byte counts, or undefined when the total is not yet known (Ollama reports
// many early progress lines with no total). Clamped so an over/under-report never escapes [0, 100].
function toPercent(completedBytes?: number, totalBytes?: number): number | undefined {
	if (totalBytes === undefined || totalBytes <= 0 || completedBytes === undefined || completedBytes < 0) {
		return undefined;
	}
	return Math.min(100, Math.max(0, (completedBytes / totalBytes) * 100));
}

// Per-call pull options. `onSuccess` fires once, only after a pull completes successfully (toast finalized + installed
// list invalidated) — never on error or on an abort — so a caller can auto-close/reset its UI without watching the
// `isPulling` transition (which also flips on failure).
interface PullOptions {
	onSuccess?: () => void;
}

interface UseModelPullResult {
	pull: (modelName: string, options?: PullOptions) => void;
	isPulling: boolean;
	progressPercent: number | undefined;
	// The model name currently being pulled (undefined when idle) — lets a list disable the in-flight row.
	pullingModelName: string | undefined;
}

// Shared single pull engine (invariant: a single code path drives every pull): BOTH the recommendation Pull button and the ModelManagement pull
// dialog drive pulls through this one hook. It consumes the hand-wired NDJSON pull stream, surfaces ONE in-place
// progress toast keyed `model-pull-${modelName}` (sticky+loading while downloading, finalized success/error), and
// invalidates the installed-models TanStack query on completion so the authoritative list refetches. The toast and
// the returned progressPercent share the same source, so the dialog's progress bar animates from the same events.
export function useModelPull(): UseModelPullResult {
	const queryClient = useQueryClient();
	const { t } = useTranslation();
	const [pullingModelName, setPullingModelName] = useState<string | undefined>();
	const [progressPercent, setProgressPercent] = useState<number | undefined>();

	// Hold the latest `t` in a ref so toast text uses the current language without making `t` a dependency of the
	// pull callback (react-i18next hands back a new `t` on language change, which would otherwise churn the callback).
	const tRef = useRef(t);
	tRef.current = t;

	// Aborts the in-flight stream on unmount so a navigation away never leaves a dangling fetch / setState.
	const abortRef = useRef<AbortController | undefined>(undefined);
	useEffect(() => {
		return () => abortRef.current?.abort();
	}, []);

	const pull = useCallback(
		(modelName: string, options?: PullOptions) => {
			const trimmed = modelName.trim();
			if (trimmed.length === 0 || pullingModelName !== undefined) {
				return;
			}

			const translate = tRef.current;
			const toastId = `model-pull-${trimmed}`;
			const title = translate("pages.models.pull.toast.title", "Pulling {{model}}", { model: trimmed });

			// Abort any prior (already-settled) controller and start a fresh one for this pull.
			abortRef.current?.abort();
			const controller = new AbortController();
			abortRef.current = controller;

			setPullingModelName(trimmed);
			setProgressPercent(undefined);
			toast.progress({
				id: toastId,
				title,
				message: translate("pages.models.pull.toast.preparing", "Preparing download…"),
			});

			const run = async (): Promise<void> => {
				try {
					// Throttle UI updates: render every phase change immediately, but coalesce the high-frequency
					// same-status byte updates to at most one per PROGRESS_THROTTLE_MS so a large pull never floods
					// React/Mantine with hundreds of re-renders per second.
					let lastStatus: string | undefined;
					let lastEmitMs = 0;
					// Track the terminal state so we never report success on a torn/aborted stream. Ollama's final
					// line is status "success"; the backend emits status "error" (with an `error` reason) when the
					// pull fails mid-stream. If the loop ends without either, the stream was torn -> treat as failure.
					let sawSuccess = false;
					for await (const event of streamModelPull(trimmed, controller.signal)) {
						// A terminal error line from the backend (sanitized reason) -> fail via the catch path below.
						if (event.status === "error" || event.error !== undefined) {
							throw new Error(event.error ?? "pull failed");
						}
						if (event.status === "success") {
							sawSuccess = true;
						}
						const statusChanged = event.status !== lastStatus;
						const now = Date.now();
						if (!statusChanged && now - lastEmitMs < PROGRESS_THROTTLE_MS) {
							continue;
						}
						lastStatus = event.status;
						lastEmitMs = now;
						const percent = toPercent(event.completedBytes, event.totalBytes);
						setProgressPercent(percent);
						toast.progress({ id: toastId, title, message: event.status, percent });
					}

					// Guard against a connection torn mid-stream (Kestrel "response already started" tear): the loop
					// ends cleanly but no "success" line ever arrived. Treat that as a failure rather than a silent
					// success toast, so the dialog/toast surface the error and the dialog becomes closable.
					if (!sawSuccess) {
						throw new Error("pull stream ended without success");
					}

					toast.success(translate("pages.models.pull.toast.success", "{{model}} is ready.", { model: trimmed }), {
						id: toastId,
						title: translate("pages.models.pull.toast.successTitle", "Model pulled"),
					});
					await queryClient.invalidateQueries({ queryKey: listLocalModelsQueryKey() });
					// Success only (not reached on error/abort): let the caller auto-close/reset its UI now the pull is done.
					options?.onSuccess?.();
				} catch (error) {
					// A pull aborted by our own cleanup (unmount) is not a user-facing failure.
					if (controller.signal.aborted) {
						return;
					}
					// Never surface the raw error.message to the user (it may carry internal/implementation detail and is
					// fragile to transport changes). Show a fixed, model-scoped i18n message; the detail goes to the console
					// for diagnostics only.
					console.warn(`model pull failed for "${trimmed}"`, error);
					toast.error(translate("pages.models.pull.toast.error", "Could not pull {{model}}.", { model: trimmed }), {
						id: toastId,
						title: translate("pages.models.pull.toast.errorTitle", "Pull failed"),
					});
				} finally {
					setPullingModelName((current) => (current === trimmed ? undefined : current));
					setProgressPercent(undefined);
				}
			};

			// Fire-and-forget: run() owns its own try/catch/finally and never rejects, but attach a no-op catch so a
			// future change can't surface an unhandled rejection.
			run().catch(() => undefined);
		},
		[pullingModelName, queryClient],
	);

	return { pull, isPulling: pullingModelName !== undefined, progressPercent, pullingModelName };
}
