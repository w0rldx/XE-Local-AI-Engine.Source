import { Alert, Anchor, Badge, Button, Divider, Group, Stack, Text } from "@mantine/core";
import { IconBrandGithub, IconInfoCircle, IconLogout, IconRefresh } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import {
	noBodyOptions,
	useAppUpdateStatus,
	useRefreshAppUpdateStatus,
	useSignOutGitHubAuth,
} from "@/features/app-update/queries/useAppUpdate";
import { GitHubSignInCard } from "@/features/app-update/components/GitHubSignInCard";
import { AppUpdateButton } from "@/features/app-update/components/AppUpdateButton";

/**
 * App-update section rendered inside the About dialog "Application" tab.
 * Only visible when `isDesktop === true`. Handles all auth states and
 * surfaces the "Check for updates" flow including sign-in, update banner,
 * and sign-out.
 */
export function AppUpdateSection() {
	const { t } = useTranslation();
	const { data: status } = useAppUpdateStatus();
	const refreshMutation = useRefreshAppUpdateStatus();
	const signOutMutation = useSignOutGitHubAuth();

	// Only render in desktop mode.
	if (!status?.isDesktop) {
		return null;
	}

	const authState = status.authState;
	// This build was never baked with a usable release repo + GitHub App client ID, so nothing in this section can do
	// any work: the check is inert server-side and sign-in would be rejected on the same predicate. Drop the controls
	// and explain instead — see the `notConfigured` case in AppUpdateAuthStateWire.
	const isConfigured = authState !== "notConfigured";

	function handleRefresh() {
		// Force a live GitHub check (?refresh=true); the default query would only re-serve the cached snapshot.
		refreshMutation.mutate();
	}

	function handleSignOut() {
		signOutMutation.mutate(noBodyOptions);
	}

	return (
		<Stack gap="sm">
			<Divider />
			<Group justify="space-between" align="center">
				<Text fw={500}>{t("pages.about.appUpdate.title")}</Text>
				{isConfigured ? (
					<Button
						variant="subtle"
						size="xs"
						leftSection={<IconRefresh size={14} />}
						loading={refreshMutation.isPending}
						onClick={handleRefresh}
					>
						{t("pages.about.appUpdate.checkForUpdates")}
					</Button>
				) : null}
			</Group>

			{/* Version info */}
			{status.currentVersion ? (
				<Group gap="xs">
					<Text size="sm" c="dimmed">
						{t("pages.about.version")}
					</Text>
					<Badge variant="light">{status.currentVersion}</Badge>
				</Group>
			) : null}

			{/* Unconfigured build — the expected resting state for the inert `main` channel, and for a tester build
			    before packaging injects the client ID. Stated plainly and without alarm; nothing here is broken. */}
			{!isConfigured ? (
				<Alert icon={<IconInfoCircle size={16} />} color="gray">
					{t("pages.about.appUpdate.notConfigured")}
				</Alert>
			) : null}

			{/* Offline notice */}
			{isConfigured && status.isOffline ? (
				<Alert icon={<IconInfoCircle size={16} />} color="yellow">
					{t("pages.about.appUpdate.offline")}
				</Alert>
			) : null}

			{/* Auth-gated content — only on a configured build, and only when online */}
			{isConfigured && !status.isOffline ? (
				<>
					{/* No repo access — operator must add this user to the release repo. */}
					{authState === "noAccess" ? (
						<Alert icon={<IconInfoCircle size={16} />} color="orange">
							{t("pages.about.appUpdate.noAccess")}
						</Alert>
					) : null}

					{/* Show sign-in card when not signed in or token needs refresh. */}
					{(authState === "signedOut" || authState === "reauthRequired") ? (
						<Stack gap="xs">
							{authState === "reauthRequired" ? (
								<Alert icon={<IconInfoCircle size={16} />} color="orange">
									{t("pages.about.appUpdate.reauthRequired")}
								</Alert>
							) : null}
							<GitHubSignInCard onAuthorized={handleRefresh} />
						</Stack>
					) : null}

					{/* Signed-in state: show login, update status, and sign-out. */}
					{authState === "signedIn" ? (
						<Stack gap="xs">
							<Group gap="xs" align="center">
								<IconBrandGithub size={14} />
								<Text size="sm" c="dimmed">
									{status.login
										? t("pages.about.appUpdate.signedInAs", { login: status.login })
										: t("pages.about.appUpdate.signedIn")}
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
							</Group>

							{/* Sign-out scope note — device-only token removal; full revoke requires GitHub settings. */}
							<Text size="xs" c="dimmed">
								{t("pages.about.appUpdate.signOutNote")}{" "}
								<Anchor
									href="https://github.com/settings/apps/authorizations"
									size="xs"
									target="_blank"
									rel="noopener noreferrer"
								>
									{t("pages.about.appUpdate.manageGitHubAccess")}
								</Anchor>
							</Text>

							{status.updateAvailable ? (
								<AppUpdateButton />
							) : (
								<Text size="sm" c="dimmed">
									{t("pages.about.appUpdate.upToDate")}
								</Text>
							)}

							{status.availableVersion ? (
								<Group gap="xs">
									<Text size="sm" c="dimmed">
										{t("pages.about.appUpdate.availableVersion")}
									</Text>
									<Badge variant="dot" color="blue">
										{status.availableVersion}
									</Badge>
								</Group>
							) : null}
						</Stack>
					) : null}
				</>
			) : null}
		</Stack>
	);
}
