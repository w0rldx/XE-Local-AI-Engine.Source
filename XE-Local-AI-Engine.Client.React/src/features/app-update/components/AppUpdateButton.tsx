import { Button, Loader, Stack, Text } from "@mantine/core";
import { IconRefresh } from "@tabler/icons-react";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { noBodyOptions, useApplyAppUpdate, useAppUpdateStatus } from "@/features/app-update/queries/useAppUpdate";

// Interval (ms) between health-live polls while waiting for the server to come back.
const HEALTH_POLL_INTERVAL_MS = 2000;
// Maximum time (ms) to wait for the server to recover before giving up.
const HEALTH_POLL_TIMEOUT_MS = 60_000;

type ApplyState = "idle" | "applying" | "restarting" | "ready" | "error";

/**
 * "Update now" button. Calls `applyAppUpdate`, then polls `/health/live` until the
 * server responds, then reloads the page. Hides when no update is available or the
 * user is not signed in.
 */
export function AppUpdateButton() {
	const { t } = useTranslation();
	const { data: status } = useAppUpdateStatus();
	const applyMutation = useApplyAppUpdate();

	const [applyState, setApplyState] = useState<ApplyState>("idle");
	const pollTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
	const pollStartRef = useRef<number>(0);

	useEffect(() => {
		return () => {
			if (pollTimerRef.current !== null) { clearTimeout(pollTimerRef.current); }
		};
	}, []);

	const isSignedIn = status?.authState === "signedIn";
	const shouldRender = status?.isDesktop === true && isSignedIn && status?.updateAvailable === true;

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
				setApplyState("ready");
				window.location.reload();
				return;
			}
		} catch {
			// Server still down — keep polling.
		}

		scheduleHealthPoll();
	}

	async function handleApply() {
		setApplyState("applying");
		try {
			await applyMutation.mutateAsync(noBodyOptions);
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
