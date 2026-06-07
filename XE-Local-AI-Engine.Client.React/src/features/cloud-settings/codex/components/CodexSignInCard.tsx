// Sign-in card for the Codex OAuth cloud provider. Rendered inside the CloudSettings page when the
// node has cloud capability. States: signed-out → pending (authorize link shown) → signed-in → expired.

import {
	ActionIcon,
	Alert,
	Badge,
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
import {
	IconAlertTriangle,
	IconCheck,
	IconCopy,
	IconExternalLink,
	IconLogin,
	IconLogout,
	IconRefresh,
} from "@tabler/icons-react";
import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { useCodexLogin, useCodexLogout, useCodexStatus } from "@/features/cloud-settings/codex/queries/useCodexAuth";

interface CodexSignInCardProps {
	/** Exposes the current signedIn state to the parent so it can gate the provider selector. */
	onSignedInChange?: (signedIn: boolean) => void;
}

export function CodexSignInCard({ onSignedInChange }: CodexSignInCardProps) {
	const { t } = useTranslation();

	// authorizeUrl is set by the login mutation and displayed while the flow is pending.
	const [authorizeUrl, setAuthorizeUrl] = useState<string | null>(null);
	const [polling, setPolling] = useState(false);
	const [pollStartedAt, setPollStartedAt] = useState<number | undefined>(undefined);

	// Detect poll timeout to surface a helpful message instead of spinning forever.
	const timedOut = polling && pollStartedAt !== undefined && Date.now() - pollStartedAt > 5 * 60 * 1_000;

	const statusQuery = useCodexStatus({ polling, pollStartedAt });

	const loginMutation = useCodexLogin(
		useCallback((url: string) => {
			setAuthorizeUrl(url);
			setPolling(true);
			setPollStartedAt(Date.now());
		}, []),
	);

	const logoutMutation = useCodexLogout(
		useCallback(() => {
			setAuthorizeUrl(null);
			setPolling(false);
			setPollStartedAt(undefined);
		}, []),
	);

	// Stop polling once a terminal state is reached.
	const status = statusQuery.data;
	useEffect(() => {
		if (status?.signedIn) {
			setPolling(false);
		}
	}, [status?.signedIn]);

	// Propagate signed-in state to the parent for provider-selector gating.
	const prevSignedIn = useRef<boolean | undefined>(undefined);
	useEffect(() => {
		const signedIn = status?.signedIn ?? false;
		if (prevSignedIn.current !== signedIn) {
			prevSignedIn.current = signedIn;
			onSignedInChange?.(signedIn);
		}
	}, [status?.signedIn, onSignedInChange]);

	const isSignedIn = status?.signedIn === true;
	const isPending = polling && !isSignedIn && !timedOut;

	function handleSignIn(): void {
		loginMutation.mutate({});
	}

	function handleSignOut(): void {
		logoutMutation.mutate({});
	}

	function handleRetry(): void {
		setAuthorizeUrl(null);
		setPolling(false);
		setPollStartedAt(undefined);
		loginMutation.reset();
		loginMutation.mutate({});
	}

	return (
		<Card withBorder={true} padding="md" radius="md">
			<Stack gap="sm">
				<Group justify="space-between" align="flex-start">
					<Stack gap={2}>
						<Text fw={600}>{t("pages.cloudSettings.codex.title")}</Text>
						<Text size="sm" c="dimmed">
							{t("pages.cloudSettings.codex.subtitle")}
						</Text>
					</Stack>
					{isSignedIn ? (
						<Badge color="green" variant="light">
							{t("pages.cloudSettings.codex.badge.signedIn")}
						</Badge>
					) : (
						<Badge color="gray" variant="light">
							{t("pages.cloudSettings.codex.badge.signedOut")}
						</Badge>
					)}
				</Group>

				{/* Signed-in state */}
				{isSignedIn && status ? (
					<Stack gap="xs">
						<Group gap="xs">
							<Text size="sm" c="dimmed">
								{t("pages.cloudSettings.codex.accountId")}:
							</Text>
							<Text size="sm" fw={500}>
								{status.accountId ?? t("pages.cloudSettings.codex.unknownAccount")}
							</Text>
						</Group>
						{status.expiresAtUtc ? (
							<Group gap="xs">
								<Text size="sm" c="dimmed">
									{t("pages.cloudSettings.codex.expires")}:
								</Text>
								<Text size="sm">{new Date(status.expiresAtUtc).toLocaleString()}</Text>
							</Group>
						) : null}
						<Button
							variant="subtle"
							color="red"
							size="xs"
							leftSection={<IconLogout size={14} />}
							onClick={handleSignOut}
							loading={logoutMutation.isPending}
							w="fit-content"
						>
							{t("pages.cloudSettings.codex.signOut")}
						</Button>
					</Stack>
				) : null}

				{/* Pending state: show authorize URL as clickable + copyable */}
				{isPending && authorizeUrl ? (
					<Stack gap="xs">
						<Alert color="blue" icon={<Loader size={14} />}>
							<Text size="sm">{t("pages.cloudSettings.codex.pendingHint")}</Text>
						</Alert>
						<TextInput
							readOnly={true}
							value={authorizeUrl}
							label={t("pages.cloudSettings.codex.authorizeUrlLabel")}
							description={t("pages.cloudSettings.codex.authorizeUrlDescription")}
							rightSection={
								<Group gap={4} wrap="nowrap">
									<CopyButton value={authorizeUrl} timeout={1500}>
										{({ copied, copy }) => (
											<Tooltip label={copied ? t("pages.cloudSettings.codex.copied") : t("pages.cloudSettings.codex.copy")}>
												<ActionIcon variant="subtle" color={copied ? "teal" : "gray"} onClick={copy}>
													{copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
												</ActionIcon>
											</Tooltip>
										)}
									</CopyButton>
									<Tooltip label={t("pages.cloudSettings.codex.openLink")}>
										<ActionIcon
											variant="subtle"
											color="blue"
											component="a"
											href={authorizeUrl}
											target="_blank"
											rel="noreferrer noopener"
										>
											<IconExternalLink size={14} />
										</ActionIcon>
									</Tooltip>
								</Group>
							}
							rightSectionWidth={64}
						/>
						<Group gap="xs">
							<Loader size="xs" />
							<Text size="sm" c="dimmed">
								{t("pages.cloudSettings.codex.polling")}
							</Text>
						</Group>
					</Stack>
				) : null}

				{/* Poll timeout */}
				{timedOut ? (
					<Alert color="orange" icon={<IconAlertTriangle size={16} />}>
						<Stack gap="xs">
							<Text size="sm">{t("pages.cloudSettings.codex.timeout")}</Text>
							<Button variant="subtle" size="xs" leftSection={<IconRefresh size={14} />} onClick={handleRetry} w="fit-content">
								{t("pages.cloudSettings.codex.retry")}
							</Button>
						</Stack>
					</Alert>
				) : null}

				{/* Login mutation error */}
				{loginMutation.isError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						<Stack gap="xs">
							<Text size="sm">{t("pages.cloudSettings.codex.loginError")}</Text>
							<Button variant="subtle" size="xs" leftSection={<IconRefresh size={14} />} onClick={handleRetry} w="fit-content">
								{t("pages.cloudSettings.codex.retry")}
							</Button>
						</Stack>
					</Alert>
				) : null}

				{/* Status query error (not login error — the polling GET failed) */}
				{statusQuery.isError && polling ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						<Text size="sm">{t("pages.cloudSettings.codex.statusError")}</Text>
					</Alert>
				) : null}

				{/* Sign-in button — shown when not signed in and not currently pending */}
				{!isSignedIn && !isPending && !timedOut && !loginMutation.isError ? (
					<Button
						variant="light"
						size="sm"
						leftSection={<IconLogin size={16} />}
						onClick={handleSignIn}
						loading={loginMutation.isPending}
						w="fit-content"
					>
						{t("pages.cloudSettings.codex.signIn")}
					</Button>
				) : null}

				{/* Egress notice — always visible to reinforce the privacy boundary */}
				<Alert color="yellow" variant="light" icon={<IconAlertTriangle size={14} />}>
					<Text size="xs">{t("pages.cloudSettings.codex.egressNotice")}</Text>
				</Alert>
			</Stack>
		</Card>
	);
}
