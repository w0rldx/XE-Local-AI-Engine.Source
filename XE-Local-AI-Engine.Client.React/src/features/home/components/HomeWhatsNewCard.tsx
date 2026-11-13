import { Spoiler, Stack, Text } from "@mantine/core";
import { IconSparkles } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";

import {
	getAppReleaseNotesOptions,
	getAppUpdateStatusOptions,
	getNodeInfoOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { MarkdownView } from "@/core/ui/components/MarkdownView/MarkdownView";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";

// The packaged release notes the last update check read from the feed. Both reads are the node's cached state: the
// status key is the one the About dialog shares (no refresh flag, so no GitHub check), and the notes endpoint never
// fetches on its own. Off desktop, before the first check, or on a build with no update source there is nothing to say,
// so the card is absent rather than empty. Both routes are unmapped off local (desktop) mode, so the status read waits
// for node info to say the node runs in it; asking first would 404 on every Home visit.
export function HomeWhatsNewCard() {
	const { t } = useTranslation();
	const nodeInfo = useQuery({ ...getNodeInfoOptions(), staleTime: Number.POSITIVE_INFINITY, retry: false });
	const status = useQuery({
		...getAppUpdateStatusOptions({ query: { refresh: null } }),
		enabled: nodeInfo.data?.isLocalMode === true,
	});
	const isDesktop = status.data?.isDesktop === true;
	const notes = useQuery({ ...getAppReleaseNotesOptions(), enabled: isDesktop });
	const items = notes.data?.items ?? [];
	// The server lists the running version first when it has notes for it, then newer versions newest first.
	const shown = items[0];
	const newest = items.find((item) => item.isNewerThanRunning);

	if (!isDesktop || shown === undefined) {
		return null;
	}

	return (
		<SectionCard
			title={t("pages.home.whatsNew.title", "What's new in {{version}}", { version: shown.version })}
			icon={<IconSparkles size={22} />}
			gap="xs"
			data-testid="home-whats-new"
		>
			{newest !== undefined ? (
				<Text size="sm" fw={500} data-testid="home-whats-new-update">
					{t("pages.home.whatsNew.updateAvailable", "Update available: {{version}}. Install it from About.", {
						version: newest.version,
					})}
				</Text>
			) : null}
			<Spoiler
				maxHeight={180}
				showLabel={t("pages.home.whatsNew.showMore", "Show more")}
				hideLabel={t("pages.home.whatsNew.showLess", "Show less")}
			>
				<Stack gap={0} data-testid="home-whats-new-notes">
					<MarkdownView content={shown.notesMarkdown} />
				</Stack>
			</Spoiler>
			{shown.isTruncated ? (
				<Text size="xs" c="dimmed" data-testid="home-whats-new-truncated">
					{t("pages.home.whatsNew.truncated", "These notes are shortened.")}
				</Text>
			) : null}
		</SectionCard>
	);
}
