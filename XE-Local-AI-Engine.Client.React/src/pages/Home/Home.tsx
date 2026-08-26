import { Button, Card, Center, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { IconDownload, IconMessageCircle } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { FullHeightPage } from "@/core/ui/components/FullHeightPage/FullHeightPage";

/**
 * The post-setup landing page.
 *
 * It used to be a single centred word. That is the first screen a tester sees after declining the onboarding tour, on
 * a node that at that moment has no models installed and therefore cannot do anything yet — so the one thing the page
 * has to carry is the next action. Which next action depends on whether there are any local models, so the page asks.
 * A failed or in-flight query is not a reason to show nothing: the model-acquisition route is offered either way,
 * because it is never the wrong place to go from here.
 */
export function Home() {
	const { t } = useTranslation();
	const { data, isLoading } = useQuery(withResponseValidation(listLocalModelsOptions()));
	const installedCount = data?.items?.length ?? 0;
	const hasModels = installedCount > 0;

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
						) : (
							<Text c="dimmed" data-testid="home-status">
								{hasModels
									? // Interpolated as `installed`, not `count`: i18next reserves `count` for plural-form key lookup,
										// and this string has no plural variants to look up.
										t("pages.home.status.ready", "{{installed}} local model(s) installed on this node.", {
											installed: installedCount,
										})
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
								variant={hasModels ? "light" : "filled"}
								leftSection={<IconDownload size={16} />}
								data-testid="home-go-to-models"
							>
								{t("pages.home.goToModels", "Install a model")}
							</Button>
							{hasModels ? (
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
