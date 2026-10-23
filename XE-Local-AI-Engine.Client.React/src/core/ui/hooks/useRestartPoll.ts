import { useCallback, useEffect, useRef, useState } from "react";

// Interval (ms) between health-live polls while waiting for the server to come back.
const HEALTH_POLL_INTERVAL_MS = 2000;
// Maximum time (ms) to wait for the server to recover before giving up.
const HEALTH_POLL_TIMEOUT_MS = 60_000;

export type RestartPollStatus = "idle" | "restarting" | "ready" | "timedOut";

export interface RestartPollOptions {
	/** The URL probed until it answers ok. Default `/health/live`. */
	readonly probePath?: string;
}

/**
 * Waits for a node that was told to restart: polls `/health/live` (or `probePath`), and once it answers asks `isBack` whether the
 * answering host is the NEW one (the old host can stay healthy while it shuts down), then reloads the page. Shared by
 * the app update and the pending-restart banner so both restarts behave the same.
 */
export function useRestartPoll(): {
	status: RestartPollStatus;
	start: (isBack: () => Promise<boolean>, options?: RestartPollOptions) => void;
} {
	const [status, setStatus] = useState<RestartPollStatus>("idle");
	const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
	// A poll re-arms the timer AFTER an awaited fetch: without this guard an unmount that lands mid-await would run the
	// cleanup first and then arm a timer nothing ever clears (a stray recurring /health/live poll).
	const mountedRef = useRef(true);

	useEffect(() => {
		mountedRef.current = true;
		return () => {
			mountedRef.current = false;
			if (timerRef.current !== null) {
				clearTimeout(timerRef.current);
			}
		};
	}, []);

	const start = useCallback((isBack: () => Promise<boolean>, options?: RestartPollOptions) => {
		const startedAt = Date.now();
		const probePath = options?.probePath ?? "/health/live";
		const schedule = () => {
			if (timerRef.current !== null) {
				clearTimeout(timerRef.current);
			}
			if (mountedRef.current) {
				timerRef.current = setTimeout(() => {
					poll();
				}, HEALTH_POLL_INTERVAL_MS);
			}
		};
		const poll = async () => {
			if (Date.now() - startedAt > HEALTH_POLL_TIMEOUT_MS) {
				setStatus("timedOut");
				return;
			}
			try {
				const response = await fetch(probePath, { cache: "no-store" });
				if (response.ok && (await isBack())) {
					setStatus("ready");
					window.location.reload();
					return;
				}
			} catch {
				// Server still down: keep polling.
			}
			schedule();
		};
		setStatus("restarting");
		schedule();
	}, []);

	return { status, start };
}
