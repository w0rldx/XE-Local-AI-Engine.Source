import { Badge, Code, Group, ScrollArea, Stack, Text } from "@mantine/core";
import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";

// Read-only live log viewer for the in-app CUDA build. Renders the current phase badge and the accumulated, scrubbed
// build log in a fixed-height monospace scroll area that auto-pins to the newest line as output streams in. Purely
// presentational — all state (phase + lines) is owned by the card via the SignalR hub / status query.

export interface CudaBuildLogViewProps {
	readonly phase: string | null;
	readonly logLines: readonly string[];
}

export function CudaBuildLogView({ phase, logLines }: CudaBuildLogViewProps) {
	const { t } = useTranslation();
	const viewportRef = useRef<HTMLDivElement>(null);

	// Auto-scroll to the newest line whenever the log grows, so streaming output stays visible without manual scrolling.
	useEffect(() => {
		const viewport = viewportRef.current;
		if (viewport === null || logLines.length === 0) {
			return;
		}
		viewport.scrollTop = viewport.scrollHeight;
	}, [logLines]);

	return (
		<Stack gap="xs" data-testid="cuda-build-log">
			<Group gap="xs" align="center">
				<Text size="xs" c="dimmed">
					{t("pages.nodeSettings.llamaCpp.cudaBuild.phaseLabel", "Phase")}
				</Text>
				{phase !== null && phase.length > 0 ? (
					<Badge size="sm" variant="light" color="primary" data-testid="cuda-build-phase">
						{phase}
					</Badge>
				) : (
					<Text size="sm" c="dimmed">
						—
					</Text>
				)}
			</Group>
			<ScrollArea h={220} viewportRef={viewportRef} data-testid="cuda-build-log-scroll">
				{logLines.length > 0 ? (
					<Code block={true} data-testid="cuda-build-log-content">
						{logLines.join("\n")}
					</Code>
				) : (
					<Text size="sm" c="dimmed" data-testid="cuda-build-log-empty">
						{t("pages.nodeSettings.llamaCpp.cudaBuild.logEmpty", "Waiting for build output…")}
					</Text>
				)}
			</ScrollArea>
		</Stack>
	);
}
