import { Alert, Anchor, Button, Code, Group, Loader, Stack, Text } from "@mantine/core";
import { IconAlertCircle, IconBrandGithub, IconLogout } from "@tabler/icons-react";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import type { XeLocalAiEngineClientEndpointsAppUpdateV1GitHubAuthStartResponse } from "@/core/api/generated";
import {
	noBodyOptions,
	usePollGitHubAuth,
	useSignOutGitHubAuth,
	useStartGitHubAuth,
} from "@/features/app-update/queries/useAppUpdate";

// Only github.com is an acceptable verification host — reject anything else before
// navigating the user away.
function isSafeVerificationUri(uri: string | undefined): boolean {
	if (!uri) { return false; }
	try {
		const parsed = new URL(uri);
		return parsed.hostname === "github.com" || parsed.hostname.endsWith(".github.com");
	} catch {
		return false;
	}
}

// Flow state driven by the device-flow response.
type FlowState = "idle" | "polling" | "authorized" | "denied" | "expired" | "error";

export interface IGitHubSignInCardProps {
	/** Called when authorization completes so the parent can refresh update status. */
	onAuthorized?: () => void;
}

/**
 * GitHub device-flow sign-in card. Calls `startGitHubAuth` to get the user code,
 * opens the verification URI (validated to github.com), then polls `pollGitHubAuth`
 * at the server-returned interval until authorized / denied / expired.
 *
 * The device_code is never held by React — it stays on the backend. The token never
 * enters React state either; the backend stores it on successful poll.
 */
export function GitHubSignInCard({ onAuthorized }: IGitHubSignInCardProps) {
	const { t } = useTranslation();
	const startMutation = useStartGitHubAuth();
	const pollMutation = usePollGitHubAuth();
	const signOutMutation = useSignOutGitHubAuth();

	const [flowState, setFlowState] = useState<FlowState>("idle");
	const [startData, setStartData] = useState<XeLocalAiEngineClientEndpointsAppUpdateV1GitHubAuthStartResponse | null>(null);
	const pollTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
	// Ref mirrors flowState so the async poll closure always reads current value.
	const flowStateRef = useRef<FlowState>("idle");

	useEffect(() => {
		flowStateRef.current = flowState;
	}, [flowState]);

	// Clear the poll timer on unmount.
	useEffect(() => {
		const timerRef = pollTimerRef;
		return () => {
			if (timerRef.current !== null) {
				clearTimeout(timerRef.current);
			}
		};
	}, []);

	function stopPolling() {
		if (pollTimerRef.current !== null) {
			clearTimeout(pollTimerRef.current);
			pollTimerRef.current = null;
		}
	}

	function schedulePoll(intervalSeconds: number) {
		stopPolling();
		pollTimerRef.current = setTimeout(async () => {
			if (flowStateRef.current !== "polling") { return; }
			try {
				const data = await pollMutation.mutateAsync(noBodyOptions);
				const state = data?.state;
				if (state === "authorized") {
					setFlowState("authorized");
					flowStateRef.current = "authorized";
					onAuthorized?.();
				} else if (state === "denied" || state === "expired") {
					setFlowState(state);
					flowStateRef.current = state;
				} else {
					// "pending" — keep polling at the current interval.
					schedulePoll(startData?.intervalSeconds ?? 5);
				}
			} catch {
				setFlowState("error");
				flowStateRef.current = "error";
			}
		}, intervalSeconds * 1000);
	}

	async function handleStartSignIn() {
		try {
			const data = await startMutation.mutateAsync(noBodyOptions);
			setStartData(data ?? null);
			setFlowState("polling");
			flowStateRef.current = "polling";
			// Open the verification URI only after validating the host.
			if (isSafeVerificationUri(data?.verificationUri)) {
				window.open(data?.verificationUri, "_blank", "noopener,noreferrer");
			}
			// Begin polling at the returned interval (default 5 s if absent).
			schedulePoll(data?.intervalSeconds ?? 5);
		} catch {
			setFlowState("error");
			flowStateRef.current = "error";
		}
	}

	function handleSignOut() {
		signOutMutation.mutate(noBodyOptions);
	}

	function handleRetry() {
		stopPolling();
		setFlowState("idle");
		flowStateRef.current = "idle";
		setStartData(null);
	}

	if (flowState === "authorized") {
		return (
			<Stack gap="xs">
				<Text size="sm" c="green">
					{t("pages.about.appUpdate.signedIn")}
				</Text>
				<Button
					variant="subtle"
					size="xs"
					leftSection={<IconLogout size={14} />}
					loading={signOutMutation.isPending}
					onClick={handleSignOut}
				>
					{t("pages.about.appUpdate.signOut")}
				</Button>
			</Stack>
		);
	}

	return (
		<Stack gap="sm">
			{/* Privacy disclosure — must be present in sign-in UI. */}
			<Text size="xs" c="dimmed">
				{t("pages.about.appUpdate.privacyNotice")}
			</Text>

			{flowState === "idle" && (
				<Button
					variant="light"
					leftSection={<IconBrandGithub size={16} />}
					loading={startMutation.isPending}
					onClick={() => { handleStartSignIn().catch((error: unknown) => console.error("sign-in failed", error)); }}
				>
					{t("pages.about.appUpdate.signInWithGitHub")}
				</Button>
			)}

			{flowState === "polling" && startData && (
				<Stack gap="xs">
					<Text size="sm">{t("pages.about.appUpdate.enterCode")}</Text>
					<Group gap="xs" align="center">
						<Code fz="lg" fw={700}>
							{startData.userCode}
						</Code>
						{isSafeVerificationUri(startData.verificationUri) ? (
							<Anchor
								href={startData.verificationUri}
								target="_blank"
								rel="noopener noreferrer"
								size="sm"
							>
								{t("pages.about.appUpdate.openGitHub")}
							</Anchor>
						) : null}
					</Group>
					<Group gap="xs" align="center">
						<Loader size="xs" />
						<Text size="xs" c="dimmed">
							{t("pages.about.appUpdate.waitingForAuth")}
						</Text>
					</Group>
				</Stack>
			)}

			{(flowState === "denied" || flowState === "expired" || flowState === "error") && (
				<Alert
					icon={<IconAlertCircle size={16} />}
					color="red"
					title={t("pages.about.appUpdate.authFailedTitle")}
				>
					<Stack gap="xs">
						<Text size="sm">
							{flowState === "denied"
								? t("pages.about.appUpdate.authDenied")
								: flowState === "expired"
									? t("pages.about.appUpdate.authExpired")
									: t("pages.about.appUpdate.authError")}
						</Text>
						<Button variant="subtle" size="xs" onClick={handleRetry}>
							{t("pages.about.appUpdate.retry")}
						</Button>
					</Stack>
				</Alert>
			)}
		</Stack>
	);
}
