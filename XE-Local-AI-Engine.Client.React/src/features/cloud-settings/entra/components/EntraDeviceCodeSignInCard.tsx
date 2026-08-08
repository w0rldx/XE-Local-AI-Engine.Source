// Device-code sign-in card for an EntraId Azure Foundry connection with no configured client secret (DeviceCode
// sign-in method). Rendered inside the CloudSettings page only for a saved EntraId+DeviceCode connection — the
// start endpoint 400s without a stored tenant + client id. Mirrors CodexSignInCard's pending/poll/timeout shape.

import {
	ActionIcon,
	Alert,
	Button,
	Card,
	CopyButton,
	Group,
	Loader,
	Stack,
	Text,
	TextInput,
	Tooltip,
} from "@mantine/core";
import { IconAlertTriangle, IconCheck, IconCopy, IconExternalLink, IconLogin, IconRefresh } from "@tabler/icons-react";
import { useCallback, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { toast } from "@/core/ui/notifications/Toast";
import {
	useEntraDeviceCodeSignIn,
	useEntraDeviceCodeStatus,
} from "@/features/cloud-settings/entra/queries/useEntraDeviceCodeAuth";

interface DeviceCode {
	userCode: string;
	verificationUri: string;
	expiresAtUtc: string;
}

export function EntraDeviceCodeSignInCard() {
	const { t } = useTranslation();

	const [deviceCode, setDeviceCode] = useState<DeviceCode | null>(null);
	// The operator's request to poll (set on sign-in start, cleared on a terminal state or retry).
	const [signInFlowActive, setSignInFlowActive] = useState(false);
	const [pollStartedAt, setPollStartedAt] = useState<number | undefined>(undefined);

	// The device code's own expiry is authoritative for the timeout — more accurate than a fixed poll window.
	const timedOut = signInFlowActive && deviceCode !== null && Date.now() > new Date(deviceCode.expiresAtUtc).getTime();

	const statusQuery = useEntraDeviceCodeStatus({ polling: signInFlowActive && !timedOut, pollStartedAt });
	const state = signInFlowActive ? statusQuery.data?.state : undefined;

	const signInMutation = useEntraDeviceCodeSignIn(
		useCallback((userCode: string, verificationUri: string, expiresAtUtc: string) => {
			setDeviceCode({ userCode, verificationUri, expiresAtUtc });
			setSignInFlowActive(true);
			setPollStartedAt(Date.now());
		}, []),
	);

	// Surfaces the terminal state as a toast exactly once, then stops polling. A side effect (toast), so it belongs
	// in an effect rather than during render.
	useEffect(() => {
		if (state === "Succeeded") {
			toast.success(t("pages.cloudSettings.entra.signInSucceeded", "Signed in with Entra ID."));
			setSignInFlowActive(false);
		} else if (state === "Failed") {
			toast.error(t("pages.cloudSettings.entra.signInFailed", "Entra ID sign-in failed."));
			setSignInFlowActive(false);
		}
	}, [state, t]);

	const isPending = signInFlowActive && !timedOut && state !== "Succeeded" && state !== "Failed";

	function handleSignIn(): void {
		signInMutation.mutate({});
	}

	function handleRetry(): void {
		setDeviceCode(null);
		setSignInFlowActive(false);
		setPollStartedAt(undefined);
		signInMutation.reset();
		signInMutation.mutate({});
	}

	return (
		<Card withBorder={true} padding="md" radius="md" data-testid="entra-device-code-sign-in-card">
			<Stack gap="sm">
				<Stack gap={2}>
					<Text fw={600}>{t("pages.cloudSettings.entra.title", "Sign in with Entra ID")}</Text>
					<Text size="sm" c="dimmed">
						{t("pages.cloudSettings.entra.subtitle", "Complete interactive sign-in for this Entra ID connection using the device-code flow.")}
					</Text>
				</Stack>

				{isPending && deviceCode ? (
					<Stack gap="xs">
						<Alert color="blue" icon={<Loader size={14} />}>
							<Text size="sm">
								{t("pages.cloudSettings.entra.pendingHint", "Enter the device code below at the verification link to complete sign-in.")}
							</Text>
						</Alert>
						<TextInput
							readOnly={true}
							value={deviceCode.userCode}
							label={t("pages.cloudSettings.entra.userCodeLabel", "Device code")}
						/>
						<TextInput
							readOnly={true}
							value={deviceCode.verificationUri}
							label={t("pages.cloudSettings.entra.verificationUriLabel", "Verification URL")}
							description={t(
								"pages.cloudSettings.entra.verificationUriDescription",
								"Open this link and enter the device code to complete sign-in.",
							)}
							rightSection={
								<Group gap={4} wrap="nowrap">
									<CopyButton value={deviceCode.verificationUri} timeout={1500}>
										{({ copied, copy }) => (
											<Tooltip
												label={
													copied
														? t("pages.cloudSettings.entra.copied", "Copied!")
														: t("pages.cloudSettings.entra.copy", "Copy link")
												}
											>
												<ActionIcon
													variant="subtle"
													color={copied ? "teal" : "gray"}
													onClick={copy}
													aria-label={
														copied
															? t("pages.cloudSettings.entra.copied", "Copied!")
															: t("pages.cloudSettings.entra.copy", "Copy link")
													}
												>
													{copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
												</ActionIcon>
											</Tooltip>
										)}
									</CopyButton>
									<Tooltip label={t("pages.cloudSettings.entra.openLink", "Open in browser")}>
										<ActionIcon
											variant="subtle"
											color="blue"
											component="a"
											href={deviceCode.verificationUri}
											target="_blank"
											rel="noreferrer noopener"
											aria-label={t("pages.cloudSettings.entra.openLink", "Open in browser")}
										>
											<IconExternalLink size={14} />
										</ActionIcon>
									</Tooltip>
								</Group>
							}
							rightSectionWidth={64}
						/>
						<Text size="sm" c="dimmed">
							{t("pages.cloudSettings.entra.expires", "Code expires")}: {new Date(deviceCode.expiresAtUtc).toLocaleString()}
						</Text>
						<Group gap="xs">
							<Loader size="xs" />
							<Text size="sm" c="dimmed">
								{t("pages.cloudSettings.entra.polling", "Waiting for sign-in to complete…")}
							</Text>
						</Group>
					</Stack>
				) : null}

				{timedOut ? (
					<Alert color="orange" icon={<IconAlertTriangle size={16} />}>
						<Stack gap="xs">
							<Text size="sm">{t("pages.cloudSettings.entra.timeout", "Sign-in timed out. The device code has expired.")}</Text>
							<Button variant="subtle" size="xs" leftSection={<IconRefresh size={14} />} onClick={handleRetry} w="fit-content">
								{t("pages.cloudSettings.entra.retry", "Try again")}
							</Button>
						</Stack>
					</Alert>
				) : null}

				{signInMutation.isError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						<Stack gap="xs">
							<Text size="sm">{t("pages.cloudSettings.entra.signInError", "Failed to start sign-in. Please try again.")}</Text>
							<Button variant="subtle" size="xs" leftSection={<IconRefresh size={14} />} onClick={handleRetry} w="fit-content">
								{t("pages.cloudSettings.entra.retry", "Try again")}
							</Button>
						</Stack>
					</Alert>
				) : null}

				{statusQuery.isError && signInFlowActive ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						<Text size="sm">{t("pages.cloudSettings.entra.statusError", "Failed to check sign-in status.")}</Text>
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
						{t("pages.cloudSettings.entra.signIn", "Sign in")}
					</Button>
				) : null}
			</Stack>
		</Card>
	);
}
