// Presentational breadcrumb timeline.
//
// Renders the ordered, already-redacted breadcrumb ring from a snapshot. Each breadcrumb category
// has its own one-line summary; no raw bodies are shown (they never reach the contract).

import { Badge, Group, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { Breadcrumb, BreadcrumbCategory } from "@/core/diagnostics/Diagnostics";

export interface BreadcrumbTimelineProps {
	readonly breadcrumbs: readonly Breadcrumb[];
}

const CATEGORY_COLORS: Record<BreadcrumbCategory, string> = {
	navigation: "blue",
	network: "teal",
	console: "yellow",
	error: "red",
	state: "grape",
	lifecycle: "gray",
};

/** One-line, redaction-safe summary for a breadcrumb. */
function summarize(breadcrumb: Breadcrumb): string {
	switch (breadcrumb.category) {
		case "navigation":
			return breadcrumb.from ? `${breadcrumb.from} → ${breadcrumb.to}` : breadcrumb.to;
		case "network":
			return `${breadcrumb.entry.method} ${breadcrumb.entry.url}${
				breadcrumb.entry.status === undefined ? "" : ` · ${breadcrumb.entry.status}`
			}`;
		case "console":
			return `[${breadcrumb.level}] ${breadcrumb.message}`;
		case "error":
			return breadcrumb.error.message;
		case "state":
			return `${breadcrumb.store}${breadcrumb.action ? ` · ${breadcrumb.action}` : ""} (${breadcrumb.diff.length})`;
		case "lifecycle":
			return breadcrumb.message;
		default: {
			// Exhaustiveness guard: every BreadcrumbCategory is handled above.
			const exhaustive: never = breadcrumb;
			return exhaustive;
		}
	}
}

function formatTime(timestamp: number): string {
	return new Date(timestamp).toLocaleTimeString();
}

export function BreadcrumbTimeline({ breadcrumbs }: BreadcrumbTimelineProps) {
	const { t } = useTranslation();

	if (breadcrumbs.length === 0) {
		return (
			<Text c="dimmed" size="sm">
				{t("diagnostics.breadcrumbs.empty")}
			</Text>
		);
	}

	return (
		<Stack gap="xs">
			{breadcrumbs.map((breadcrumb) => (
				<Group key={breadcrumb.id} gap="sm" wrap="nowrap" align="flex-start">
					<Text c="dimmed" size="xs" ff="monospace" style={{ flexShrink: 0 }}>
						{formatTime(breadcrumb.timestamp)}
					</Text>
					<Badge color={CATEGORY_COLORS[breadcrumb.category]} variant="light" size="sm" style={{ flexShrink: 0 }}>
						{breadcrumb.category}
					</Badge>
					<Text size="sm" style={{ wordBreak: "break-word" }}>
						{summarize(breadcrumb)}
					</Text>
				</Group>
			))}
		</Stack>
	);
}
