import { Button, Loader, Stack, Text } from "@mantine/core";
import { IconRefresh } from "@tabler/icons-react";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import {
	noBodyOptions,
	useApplyAppUpdate,
	useAppUpdateStatus,
	useProbeAppUpdateStatus,
} from "@/features/app-update/queries/useAppUpdate";

// Interval (ms) between health-live polls while waiting for the server to come back.
const HEALTH_POLL_INTERVAL_MS = 2000;
// Maximum time (ms) to wait for the server to recover before giving up.
const HEALTH_POLL_TIMEOUT_MS = 60_000;

type ApplyState = "idle" | "applying" | "restarting" | "ready" | "error";

/**
 * Owns the full ready-state update UI. Keeping this component mounted while an update is applying is load-bearing:
 * the old host clears its cached availability before exit, but restart polling must continue until the new version
 * answers. Calls `applyAppUpdate`, probes `/health/live` plus the side-effect-free version endpoint, then reloads.
 */
export function AppUpdateButton() {
	const { t } = useTranslation();
	const { data: status } = useAppUpdateStatus();
	const applyMutation = useApplyAppUpdate();
	const probeStatusMutation = useProbeAppUpdateStatus();

	const [applyState, setApplyState] = useState<ApplyState>("idle");
	const pollTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
	const pollStartRef = useRef<number>(0);
	const expectedVersionRef = useRef<string | null>(null);
	// doHealthPoll re-arms the timer AFTER an awaited fetch — without this guard, an unmount that lands mid-await
	// would run the cleanup first and then arm a timer nothing ever clears (a stray recurring /health/live poll).
	const mountedRef = useRef(true);

	useEffect(() => {
		const timerRef = pollTimerRef;
		mountedRef.current = true;
		return () => {
			mountedRef.current = false;
			if (timerRef.current !== null) { clearTimeout(timerRef.current); }
		};
	}, []);

	const availableVersion = status?.availableVersion ?? null;
	const shouldRender = status?.isDesktop === true && status.isConfigured === true;

	if (!shouldRender) {
		return null;
	}

	function stopPolling() {
		if (pollTimerRef.current !== null) {
			clearTimeout(pollTimerRef.current);
			pollTimerRef.current = null;
		}
	}

	function scheduleHealthPoll() {
		stopPolling();
		if (!mountedRef.current) {
			return;
		}
		pollTimerRef.current = setTimeout(() => {
			doHealthPoll();
		}, HEALTH_POLL_INTERVAL_MS);
	}

	async function doHealthPoll() {
		const elapsed = Date.now() - pollStartRef.current;
		if (elapsed > HEALTH_POLL_TIMEOUT_MS) {
			setApplyState("error");
			return;
		}

		try {
			const response = await fetch("/health/live", { cache: "no-store" });
			if (response.ok) {
				const probedStatus = await probeStatusMutation.mutateAsync();
				if (probedStatus.currentVersion === expectedVersionRef.current) {
					setApplyState("ready");
					window.location.reload();
					return;
				}
			}
		} catch {
			// Server still down — keep polling.
		}

		scheduleHealthPoll();
	}

	async function handleApply() {
		if (!availableVersion) {
			setApplyState("error");
			return;
		}
		setApplyState("applying");
		try {
			const result = await applyMutation.mutateAsync(noBodyOptions);
			if (!result.applying) {
				setApplyState("idle");
				return;
			}
			expectedVersionRef.current = availableVersion;
			setApplyState("restarting");
			pollStartRef.current = Date.now();
			scheduleHealthPoll();
		} catch {
			setApplyState("error");
		}
	}

	if (applyState === "restarting") {
		return (
			<Stack gap="xs" align="center">
				<Loader size="sm" />
				<Text size="sm" c="dimmed">
					{t("pages.about.appUpdate.restarting")}
				</Text>
			</Stack>
		);
	}

	if (applyState === "error") {
		return (
			<Text size="sm" c="red">
				{t("pages.about.appUpdate.applyError")}
			</Text>
		);
	}

	if (applyState === "idle" && !status.updateAvailable) {
		return (
			<Text size="sm" c="dimmed">
				{t("pages.about.appUpdate.upToDate")}
			</Text>
		);
	}

	return (
		<Button
			variant="filled"
			leftSection={<IconRefresh size={16} />}
			loading={applyState === "applying"}
			onClick={() => { handleApply().catch((error: unknown) => console.error("apply failed", error)); }}
		>
			{t("pages.about.appUpdate.updateNow")}
		</Button>
	);
}
