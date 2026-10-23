import { Button, Card, Center, Group, Loader, Progress, Stack, Text, Title } from "@mantine/core";
import { IconDownload, IconMessageCircle } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { FullHeightPage } from "@/core/ui/components/FullHeightPage/FullHeightPage";
import { toExternalModelOptions } from "@/features/chat/pages/ChatModelOptions";
import { isTerminalAcquisitionPhase } from "@/features/models/models/GgufAcquisitionModels";
import { isInstalledLocalModel } from "@/features/models/models/LocalModelMappers";
import { useActiveGgufDownloads } from "@/features/models/queries/useGgufDownload";

/**
 * The post-setup landing page.
 *
 * This is the first screen a tester sees after declining the onboarding tour, on
 * a node that at that moment has no models installed and therefore cannot do anything yet — so the one thing the page
 * has to carry is the next action. Which next action depends on what this node can actually send to, so the page asks.
 * A failed or in-flight query is not a reason to show nothing: the model-acquisition route is offered either way,
 * because it is never the wrong place to go from here.
 */
export function Home() {
	const { t } = useTranslation();
	const { data, isLoading } = useQuery(listLocalModelsOptions());
	const items = data?.items ?? [];
	// External-provider registrations ride the same list but are not installed on this node, and the /models route this
	// card points at does not list them — counting them as installed would promise models that page cannot show.
	const installedCount = items.filter(isInstalledLocalModel).length;
	const hasModels = installedCount > 0;
	// They ARE a usable send path, though, and an external-only node is a supported shape. Counted with the chat
	// page's own helper (chat-kind external registrations) so the two screens can never disagree about whether this
	// node can answer anything; the second argument is the same `true` the picker passes — an external endpoint is
	// reachable independently of the local runtime's availability flag.
	const canChat = hasModels || toExternalModelOptions(items, true).length > 0;
	// First-run provisioning downloads the default model through the shared download coordinator. The feed is pushed
	// over the download hub (with that hook's own bounded fallback), so a node mid-setup says so instead of "install one".
	const downloads = useActiveGgufDownloads();
	const setupDownload = hasModels
		? undefined
		: [...downloads.values()].find((download) => !isTerminalAcquisitionPhase(download.phase));

	return (
		// The only centred, non-scrolling page in the app, but still a routed page: it goes through the shared
		// full-height frame so its outer margins and vertical rhythm match every other route (FullHeightPage owns
		// the py="lg" the rest of the app uses), with the Center claiming the remaining height to keep the card
		// visually centred exactly as before.
		<FullHeightPage>
			<Center w="100%" p="md" style={{ flex: 1, minHeight: 0 }}>
				<Card withBorder={true} radius="lg" p="xl" maw={560} w="100%" data-testid="home-card">
					<Stack gap="md" align="center" ta="center">
						<Title order={1}>{t("pages.home.title.welcome")}</Title>

						{isLoading ? (
							<Loader size="sm" aria-label={t("pages.home.loading", "Checking installed models")} />
						) : setupDownload !== undefined ? (
							<Stack gap="xs" w="100%" data-testid="home-setup">
								<Text fw={500}>{t("pages.home.setup.title", "Setting up your first model…")}</Text>
								<Text c="dimmed" size="sm" data-testid="home-setup-model">
									{setupDownload.pct === undefined
										? setupDownload.modelName
										: t("pages.home.setup.progress", "{{model}} · {{pct}}%", {
												model: setupDownload.modelName,
												pct: setupDownload.pct,
											})}
								</Text>
								<Progress
									value={setupDownload.pct ?? 100}
									animated={setupDownload.pct === undefined}
									aria-label={t("pages.home.setup.title", "Setting up your first model…")}
								/>
							</Stack>
						) : (
							<Text c="dimmed" data-testid="home-status">
								{hasModels
									? // Interpolated as `installed`, not `count`: i18next reserves `count` for plural-form key lookup,
										// and this string has no plural variants to look up.
										t("pages.home.status.ready", "{{installed}} local model(s) installed on this node.", {
											installed: installedCount,
										})
									: canChat
										? t(
												"pages.home.status.externalOnly",
												"No local models are installed, but this node can chat through a registered external provider.",
											)
										: t(
												"pages.home.status.empty",
												"No local models are installed yet, so this node cannot answer anything. Install one to get started.",
											)}
							</Text>
						)}

						<Group justify="center">
							<Button
								component={Link}
								to="/models"
								variant={canChat || setupDownload !== undefined ? "light" : "filled"}
								leftSection={<IconDownload size={16} />}
								data-testid="home-go-to-models"
							>
								{setupDownload === undefined
									? t("pages.home.goToModels", "Install a model")
									: t("pages.home.setup.viewModels", "View on the Models page")}
							</Button>
							{canChat ? (
								<Button component={Link} to="/chat" leftSection={<IconMessageCircle size={16} />} data-testid="home-go-to-chat">
									{t("pages.home.goToChat", "Start chatting")}
								</Button>
							) : null}
						</Group>
					</Stack>
				</Card>
			</Center>
		</FullHeightPage>
	);
}
