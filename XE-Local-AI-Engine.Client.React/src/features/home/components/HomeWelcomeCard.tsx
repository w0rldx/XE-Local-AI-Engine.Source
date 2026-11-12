import { Button, Group, Loader, Progress, Stack, Text } from "@mantine/core";
import { IconDownload, IconMessageCircle, IconPlayerPlay, IconRefresh } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { isTerminalAcquisitionPhase } from "@/core/model-downloads/models/GgufAcquisitionModels";
import {
	type GgufDownloadStatus,
	useActiveGgufDownloads,
	useStartGgufDownload,
} from "@/core/model-downloads/queries/useGgufDownload";
import { hasAnyChatModel } from "@/core/models/ChatModelOptions";
import { ButtonLink } from "@/core/ui/components/ButtonLink/ButtonLink";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { useChatHandOff } from "@/features/home/hooks/useChatHandOff";
import { greetingPeriod, setupDownloadPercent } from "@/features/home/models/HomeModels";
import { useRecentConversations } from "@/features/home/queries/useRecentConversations";

// The greeting, the two ways into chat, and, while this node cannot answer anything yet, what to do about that. A
// failed model read is not a reason to hide the actions: chat and the models page are never the wrong place to go.
export function HomeWelcomeCard() {
	const { t } = useTranslation();
	const [hour] = useState(() => new Date().getHours());
	const { openConversation, startNewConversation } = useChatHandOff();
	const models = useQuery(listLocalModelsOptions());
	const recent = useRecentConversations();
	const lastConversation = recent.data?.[0];
	const needsModel = models.data !== undefined && !hasAnyChatModel(models.data.items ?? []);
	// First-run provisioning downloads the default model through the shared download coordinator (hub-fed).
	const downloads = useActiveGgufDownloads();
	const setupDownload = needsModel
		? [...downloads.values()].find((download) => !isTerminalAcquisitionPhase(download.phase))
		: undefined;
	const setupPercent = setupDownload === undefined ? undefined : setupDownloadPercent(setupDownload);
	// A failed download (the server keeps the row for a day, and records an interrupted one after a restart) is offered
	// again under its `{repo}:{quant}` name and start options; the coordinator resumes the part already on disk. Of
	// several, the newest.
	const failedDownload =
		needsModel && setupDownload === undefined
			? [...downloads.values()]
					.filter((download) => download.phase === "Failed")
					.toSorted((a, b) => (Date.parse(b.updatedAtUtc ?? "") || 0) - (Date.parse(a.updatedAtUtc ?? "") || 0))[0]
			: undefined;
	const startDownload = useStartGgufDownload();
	// Replays the row's own start options. A row without them (recorded after a restart) is never retried with guessed
	// defaults: the projector choice is part of the installed identity, so the operator starts it again on the Models page.
	const retryFailedDownload = (download: GgufDownloadStatus): void => {
		const colon = download.modelName.lastIndexOf(":");
		startDownload.mutate({
			...(colon < 0
				? { repoId: download.modelName }
				: { repoId: download.modelName.slice(0, colon), quant: download.modelName.slice(colon + 1) }),
			fileName: download.fileName ?? undefined,
			revision: download.revision ?? undefined,
			includeProjector: download.includeProjector ?? undefined,
		});
	};

	const greeting = {
		morning: t("pages.home.welcome.greeting.morning", "Good morning"),
		afternoon: t("pages.home.welcome.greeting.afternoon", "Good afternoon"),
		evening: t("pages.home.welcome.greeting.evening", "Good evening"),
	}[greetingPeriod(hour)];

	return (
		<SectionCard title={greeting} data-testid="home-welcome">
			{models.isLoading ? (
				<Loader size="sm" aria-label={t("pages.home.welcome.loading", "Checking installed models")} />
			) : setupDownload !== undefined ? (
				<Stack gap="xs" data-testid="home-setup">
					<Text fw={500}>{t("pages.home.welcome.setup.title", "Setting up your first model…")}</Text>
					<Text c="dimmed" size="sm" data-testid="home-setup-model">
						{setupPercent === undefined
							? setupDownload.modelName
							: t("pages.home.welcome.setup.progress", "{{model}} · {{pct}}%", {
									model: setupDownload.modelName,
									pct: setupPercent,
								})}
					</Text>
					<Progress
						value={setupPercent ?? 100}
						animated={setupPercent === undefined}
						aria-label={t("pages.home.welcome.setup.title", "Setting up your first model…")}
					/>
				</Stack>
			) : failedDownload !== undefined ? (
				<Stack gap="xs" data-testid="home-setup-failed">
					<Text fw={500}>
						{t("pages.home.welcome.downloadFailedTitle", "{{model}} did not finish downloading.", {
							model: failedDownload.modelName,
						})}
					</Text>
					{failedDownload.sanitizedError ? (
						<Text c="dimmed" size="sm" data-testid="home-setup-failed-reason">
							{failedDownload.sanitizedError}
						</Text>
					) : null}
					{startDownload.isError ? (
						<Text c="red" size="sm" role="alert" data-testid="home-setup-retry-error">
							{t(
								"pages.home.welcome.downloadFailedRetryError",
								"The download could not be started again. Try once more, or install a model from the Models page.",
							)}
						</Text>
					) : null}
					{failedDownload.includeProjector == null ? (
						<>
							<Text c="dimmed" size="sm" data-testid="home-setup-failed-restart">
								{t("pages.home.welcome.downloadFailedRestart", "This download has to be started again from the Models page.")}
							</Text>
							<Group>
								<ButtonLink
									to={nodeRoutePaths.models}
									leftSection={<IconDownload size={16} />}
									data-testid="home-setup-open-models"
								>
									{t("pages.home.welcome.downloadFailedOpenModels", "Open the Models page")}
								</ButtonLink>
							</Group>
						</>
					) : (
						<Group>
							<Button
								leftSection={<IconRefresh size={16} />}
								loading={startDownload.isPending}
								onClick={() => retryFailedDownload(failedDownload)}
								data-testid="home-setup-retry"
							>
								{t("pages.home.welcome.downloadFailedRetry", "Retry download")}
							</Button>
						</Group>
					)}
				</Stack>
			) : needsModel ? (
				<Text c="dimmed" data-testid="home-no-model">
					{t(
						"pages.home.welcome.noModel",
						"No chat model is installed yet, so this node cannot answer anything. Install one to get started.",
					)}
				</Text>
			) : (
				<Text c="dimmed">{t("pages.home.welcome.ready", "Start a new chat or pick up where you left off.")}</Text>
			)}
			<Group gap="sm">
				<Button
					variant={needsModel ? "light" : "filled"}
					leftSection={<IconMessageCircle size={16} />}
					onClick={startNewConversation}
					data-testid="home-new-chat"
				>
					{t("pages.home.welcome.newChat", "New chat")}
				</Button>
				{lastConversation === undefined ? null : (
					<Button
						variant="light"
						leftSection={<IconPlayerPlay size={16} />}
						onClick={() => openConversation(lastConversation.id)}
						data-testid="home-continue"
					>
						{t("pages.home.welcome.continue", "Continue last chat")}
					</Button>
				)}
				{needsModel ? (
					<ButtonLink
						to={nodeRoutePaths.models}
						variant={setupDownload === undefined && failedDownload === undefined ? "filled" : "light"}
						leftSection={<IconDownload size={16} />}
						data-testid="home-go-to-models"
					>
						{t("pages.home.welcome.installModel", "Install a model")}
					</ButtonLink>
				) : null}
			</Group>
		</SectionCard>
	);
}
