import { Group, Skeleton, Stack, Text } from "@mantine/core";
import { IconCalendarTime } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { getErrorStatus } from "@/core/api/errors/RetryClassification";
import { listUpcomingScheduledJobsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { ButtonLink } from "@/core/ui/components/ButtonLink/ButtonLink";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";

const UPCOMING_LIMIT = 5;
// Home is a glance with no scheduler hub of its own; the scheduler's job mutations invalidate this key, so the poll
// only catches fires and changes made elsewhere. Next fire times move by the minute at most, so 60 s is enough.
const UPCOMING_REFRESH_MS = 60_000;
const fireTimeFormat: Intl.DateTimeFormatOptions = { dateStyle: "medium", timeStyle: "short" };

// The next scheduled executions, soonest first as the server sorts them. The scheduler routes answer 404 while the
// node's scheduler switch is off, so a 404 hides the card like a build without the capability does.
export function HomeUpcomingCard() {
	const { t } = useTranslation();
	const upcoming = useQuery({
		...listUpcomingScheduledJobsOptions({ query: { limit: UPCOMING_LIMIT } }),
		enabled: nodeCapabilities.scheduler,
		refetchInterval: (query) => (getErrorStatus(query.state.error) === 404 ? false : UPCOMING_REFRESH_MS),
	});

	if (!nodeCapabilities.scheduler || getErrorStatus(upcoming.error) === 404) {
		return null;
	}
	const items = upcoming.data?.items ?? [];

	return (
		<SectionCard
			title={t("pages.home.upcoming.title", "Upcoming")}
			icon={<IconCalendarTime size={22} />}
			gap="xs"
			actions={
				<ButtonLink to={nodeRoutePaths.scheduler} variant="subtle" size="compact-sm" data-testid="home-upcoming-link">
					{t("pages.home.upcoming.open", "Open scheduler")}
				</ButtonLink>
			}
			data-testid="home-upcoming"
		>
			{upcoming.isLoading ? (
				<Stack gap="xs">
					<Skeleton height={28} />
					<Skeleton height={28} />
				</Stack>
			) : upcoming.isError ? (
				<InlineErrorAlert
					message={apiErrorMessage(upcoming.error, t("pages.home.upcoming.loadError", "Upcoming jobs could not be loaded."))}
					data-testid="home-upcoming-error"
				/>
			) : items.length === 0 ? (
				<Text c="dimmed" size="sm" data-testid="home-upcoming-empty">
					{t("pages.home.upcoming.empty", "Nothing scheduled")}
				</Text>
			) : (
				<Stack gap="xs">
					{items.map((job) => (
						<Group key={job.scheduledJobId} justify="space-between" wrap="nowrap" gap="sm" data-testid="home-upcoming-item">
							<Text size="sm" fw={500} truncate={true}>
								{job.displayName}
							</Text>
							<Text size="sm" c="dimmed" style={{ whiteSpace: "nowrap" }}>
								{formatTimestamp(job.nextFireTimeUtc, fireTimeFormat)}
							</Text>
						</Group>
					))}
				</Stack>
			)}
		</SectionCard>
	);
}
