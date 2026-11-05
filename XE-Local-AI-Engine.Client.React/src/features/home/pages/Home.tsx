import { SimpleGrid } from "@mantine/core";
import { IconHome } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { useUiMode } from "@/core/layout/hooks/useUiMode";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { HomeActivityCard } from "@/features/home/components/HomeActivityCard";
import { HomeRecentChatsCard } from "@/features/home/components/HomeRecentChatsCard";
import { HomeUpcomingCard } from "@/features/home/components/HomeUpcomingCard";
import { HomeUsageCard } from "@/features/home/components/HomeUsageCard";
import { HomeWelcomeCard } from "@/features/home/components/HomeWelcomeCard";
import { HomeWhatsNewCard } from "@/features/home/components/HomeWhatsNewCard";

// The landing workspace: a fixed set of cards, each owning its own loading, empty and error state so one failing card
// never takes the page down. Simple mode shows the everyday cards; Advanced adds the operator's. The grid adds its
// second column at `lg`, not `md`, because the page sits beside the sidebar.
export function Home() {
	const { t } = useTranslation();
	const advanced = useUiMode() === "advanced";

	return (
		<PageShell data-testid="home-page">
			<PageHeader
				title={t("pages.home.title", "Home")}
				icon={<IconHome size={24} />}
				subtitle={t("pages.home.subtitle", "Pick up a chat and see what this node has been doing.")}
			/>
			<HomeWelcomeCard />
			<SimpleGrid cols={{ base: 1, lg: 2 }} spacing="lg">
				<HomeActivityCard />
				<HomeRecentChatsCard />
				{advanced ? <HomeUpcomingCard /> : null}
				{advanced ? <HomeUsageCard /> : null}
				<HomeWhatsNewCard />
			</SimpleGrid>
		</PageShell>
	);
}
