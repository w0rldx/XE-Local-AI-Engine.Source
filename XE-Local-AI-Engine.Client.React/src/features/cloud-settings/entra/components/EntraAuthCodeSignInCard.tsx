// Authorization-code sign-in card for an EntraId Azure Foundry connection with the AuthorizationCode sign-in
// method (confidential client + PKCE, "Postman parity"): browser sign-in yields a delegated token while the stored
// client secret authenticates the code redemption. Rendered inside the CloudSettings page only for a saved
// EntraId+AuthorizationCode connection — the start endpoint 400s without a stored tenant/client/secret. Mirrors
// EntraDeviceCodeSignInCard's pending/poll/timeout shape; opens the authorize URL in a new tab instead of showing a
// device code.

import { Alert, Button, Card, Group, Loader, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconExternalLink, IconLogin, IconRefresh } from "@tabler/icons-react";
import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { toast } from "@/core/ui/notifications/Toast";
import {
	useEntraAuthCodeSignIn,
	useEntraAuthCodeStatus,
} from "@/features/cloud-settings/entra/queries/useEntraAuthCodeAuth";

interface PendingAttempt {
	authorizeUrl: string;
	expiresAtUtc: string;
}

export function EntraAuthCodeSignInCard() {
	const { t } = useTranslation();

	const [pendingAttempt, setPendingAttempt] = useState<PendingAttempt | null>(null);
	// The operator's request to poll (set on sign-in start, cleared on a terminal state or retry).
	const [signInFlowActive, setSignInFlowActive] = useState(false);
	const [pollStartedAt, setPollStartedAt] = useState<number | undefined>(undefined);

	// The attempt's own expiry is authoritative for the timeout — more accurate than a fixed poll window.
	const timedOut = signInFlowActive && pendingAttempt !== null && Date.now() > new Date(pendingAttempt.expiresAtUtc).getTime();

	const statusQuery = useEntraAuthCodeStatus({ polling: signInFlowActive && !timedOut, pollStartedAt });
	const state = signInFlowActive ? statusQuery.data?.state : undefined;

	const signInMutation = useEntraAuthCodeSignIn(
		useCallback((authorizeUrl: string, expiresAtUtc: string) => {
			setPendingAttempt({ authorizeUrl, expiresAtUtc });
			setSignInFlowActive(true);
			setPollStartedAt(Date.now());
			window.open(authorizeUrl, "_blank", "noopener,noreferrer");
		}, []),
	);

	// Surfaces the terminal state as a toast exactly once, then stops polling. A side effect (toast), so it belongs
	// in an effect rather than during render.
	useEffect(() => {
		if (state === "Succeeded") {
			toast.success(t("pages.cloudSettings.entra.authCode.signInSucceeded", "Signed in with Entra ID."));
			setSignInFlowActive(false);
		} else if (state === "Failed") {
			toast.error(t("pages.cloudSettings.entra.authCode.signInFailed", "Entra ID sign-in failed."));
			setSignInFlowActive(false);
		}
	}, [state, t]);

	const isPending = signInFlowActive && !timedOut && state !== "Succeeded" && state !== "Failed";

	function handleSignIn(): void {
		signInMutation.mutate({});
	}

	function handleRetry(): void {
		setPendingAttempt(null);
		setSignInFlowActive(false);
		setPollStartedAt(undefined);
		signInMutation.reset();
		signInMutation.mutate({});
	}

	function handleReopen(): void {
		if (pendingAttempt !== null) {
			window.open(pendingAttempt.authorizeUrl, "_blank", "noopener,noreferrer");
		}
	}

	return (
		<Card withBorder={true} padding="md" radius="md" data-testid="entra-auth-code-sign-in-card">
			<Stack gap="sm">
				<Stack gap={2}>
					<Text fw={600}>{t("pages.cloudSettings.entra.authCode.title", "Sign in with Entra ID")}</Text>
					<Text size="sm" c="dimmed">
						{t(
							"pages.cloudSettings.entra.authCode.subtitle",
							"Complete interactive sign-in for this Entra ID connection in a browser tab.",
						)}
					</Text>
				</Stack>

				{isPending ? (
					<Stack gap="xs">
						<Alert color="blue" icon={<Loader size={14} />}>
							<Text size="sm">
								{t(
									"pages.cloudSettings.entra.authCode.pendingHint",
									"A browser tab opened to complete sign-in. Waiting for it to finish…",
								)}
							</Text>
						</Alert>
						<Group gap="xs">
							<Button
								variant="subtle"
								size="xs"
								leftSection={<IconExternalLink size={14} />}
								onClick={handleReopen}
								w="fit-content"
							>
								{t("pages.cloudSettings.entra.authCode.reopen", "Reopen sign-in tab")}
							</Button>
						</Group>
					</Stack>
				) : null}

				{timedOut ? (
					<Alert color="orange" icon={<IconAlertTriangle size={16} />}>
						<Stack gap="xs">
							<Text size="sm">{t("pages.cloudSettings.entra.authCode.timeout", "Sign-in timed out.")}</Text>
							<Button variant="subtle" size="xs" leftSection={<IconRefresh size={14} />} onClick={handleRetry} w="fit-content">
								{t("pages.cloudSettings.entra.authCode.retry", "Try again")}
							</Button>
						</Stack>
					</Alert>
				) : null}

				{signInMutation.isError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						<Stack gap="xs">
							<Text size="sm">{t("pages.cloudSettings.entra.authCode.signInError", "Failed to start sign-in. Please try again.")}</Text>
							<Button variant="subtle" size="xs" leftSection={<IconRefresh size={14} />} onClick={handleRetry} w="fit-content">
								{t("pages.cloudSettings.entra.authCode.retry", "Try again")}
							</Button>
						</Stack>
					</Alert>
				) : null}

				{statusQuery.isError && signInFlowActive ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						<Text size="sm">{t("pages.cloudSettings.entra.authCode.statusError", "Failed to check sign-in status.")}</Text>
					</Alert>
				) : null}

				{!isPending && !timedOut && !signInMutation.isError ? (
					<Button
						variant="light"
						size="sm"
						leftSection={<IconLogin size={16} />}
						onClick={handleSignIn}
						loading={signInMutation.isPending}
						w="fit-content"
					>
						{t("pages.cloudSettings.entra.authCode.signIn", "Sign in")}
					</Button>
				) : null}
			</Stack>
		</Card>
	);
}
