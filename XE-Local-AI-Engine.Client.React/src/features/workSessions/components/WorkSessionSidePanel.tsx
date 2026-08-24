import { Paper, ScrollArea, Tabs } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { WorkSessionArtifactsTab } from "@/features/workSessions/components/WorkSessionArtifactsTab";
import { WorkSessionCheckpointsTab } from "@/features/workSessions/components/WorkSessionCheckpointsTab";
import { WorkSessionEventsTab } from "@/features/workSessions/components/WorkSessionEventsTab";
import { WorkSessionFindingsTab } from "@/features/workSessions/components/WorkSessionFindingsTab";
import type {
	WorkSessionArtifactResponse,
	WorkSessionCheckpointResponse,
	WorkSessionEventResponse,
	WorkSessionFindingResponse,
	WorkSessionStatus,
} from "@/features/workSessions/models/WorkSessionModels";

export interface WorkSessionSidePanelProps {
	readonly sessionId: string;
	readonly status: WorkSessionStatus;
	readonly findings: readonly WorkSessionFindingResponse[];
	readonly artifacts: readonly WorkSessionArtifactResponse[];
	readonly checkpoints: readonly WorkSessionCheckpointResponse[];
	readonly events: readonly WorkSessionEventResponse[];
	readonly hasMoreEvents: boolean;
	readonly canLoadMoreEvents: boolean;
	readonly onLoadMoreEvents: () => void;
}

/** Where the panel opens: a finished session on its report, a failed one on what went wrong, otherwise the findings. */
function initialTab(status: WorkSessionStatus): string {
	if (status === "Completed") {
		return "artifacts";
	}
	if (status === "Failed") {
		return "events";
	}
	return "findings";
}

export function WorkSessionSidePanel({
	sessionId,
	status,
	findings,
	artifacts,
	checkpoints,
	events,
	hasMoreEvents,
	canLoadMoreEvents,
	onLoadMoreEvents,
}: WorkSessionSidePanelProps) {
	const { t } = useTranslation();
	const [tab, setTab] = useState<string | null>(() => initialTab(status));

	return (
		<Paper withBorder={true} p="md" h="100%" data-testid="work-session-side-panel" style={{ display: "flex", flexDirection: "column", minHeight: 0 }}>
			<Tabs value={tab} onChange={setTab} style={{ display: "flex", flexDirection: "column", flex: 1, minHeight: 0 }}>
				<Tabs.List>
					<Tabs.Tab value="findings" data-testid="work-session-tab-findings">
						{t("pages.workSessions.tabs.findings", "Findings")}
					</Tabs.Tab>
					<Tabs.Tab value="artifacts" data-testid="work-session-tab-artifacts">
						{t("pages.workSessions.tabs.artifacts", "Artifacts")}
					</Tabs.Tab>
					<Tabs.Tab value="checkpoints" data-testid="work-session-tab-checkpoints">
						{t("pages.workSessions.tabs.checkpoints", "Checkpoints")}
					</Tabs.Tab>
					<Tabs.Tab value="events" data-testid="work-session-tab-events">
						{t("pages.workSessions.tabs.events", "Events")}
					</Tabs.Tab>
				</Tabs.List>
				<ScrollArea style={{ flex: 1, minHeight: 0 }} pt="sm">
					<Tabs.Panel value="findings">
						<WorkSessionFindingsTab findings={findings} />
					</Tabs.Panel>
					<Tabs.Panel value="artifacts">
						<WorkSessionArtifactsTab sessionId={sessionId} artifacts={artifacts} preselectReport={status === "Completed"} />
					</Tabs.Panel>
					<Tabs.Panel value="checkpoints">
						<WorkSessionCheckpointsTab checkpoints={checkpoints} />
					</Tabs.Panel>
					<Tabs.Panel value="events">
						<WorkSessionEventsTab
							events={events}
							hasMore={hasMoreEvents}
							canLoadMore={canLoadMoreEvents}
							onLoadMore={onLoadMoreEvents}
						/>
					</Tabs.Panel>
				</ScrollArea>
			</Tabs>
		</Paper>
	);
}
