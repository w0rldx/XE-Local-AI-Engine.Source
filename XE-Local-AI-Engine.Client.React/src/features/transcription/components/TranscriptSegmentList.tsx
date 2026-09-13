import { Badge, Group, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { TranscriptSegmentView } from "@/features/transcription/models/TranscriptionModels";

interface TranscriptSegmentListProps {
	readonly segments: readonly TranscriptSegmentView[];
}

/**
 * Formats a segment boundary as `mm:ss.S`.
 *
 * Deliberately not `core/formatting/TimeFormatting`: every helper there formats a DURATION or a wall-clock instant,
 * and this is an offset INTO the clip — the reader scrubs to it, so the tenth of a second is the point.
 */
function formatClipOffset(milliseconds: number): string {
	const totalSeconds = Math.max(milliseconds, 0) / 1000;
	const minutes = Math.floor(totalSeconds / 60);
	const seconds = Math.floor(totalSeconds % 60);
	const tenths = Math.floor((totalSeconds * 10) % 10);
	return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}.${tenths}`;
}

/**
 * The committed transcript: one row per segment, in Seq order.
 *
 * A channel badge shows only for a non-Mono segment — a single-source recording has nothing to attribute, and a
 * "Mono" badge on every row would be noise.
 */
export function TranscriptSegmentList({ segments }: TranscriptSegmentListProps) {
	const { t } = useTranslation();

	return (
		<Stack gap="xs" data-testid="transcript-segment-list">
			{segments.map((segment) => (
				<Group key={segment.id} gap="sm" align="flex-start" wrap="nowrap" data-testid={`transcript-segment-${segment.seq}`}>
					<Text size="xs" c="dimmed" ff="monospace" style={{ flex: "0 0 auto" }}>
						{`${formatClipOffset(segment.startMs)} – ${formatClipOffset(segment.endMs)}`}
					</Text>
					{segment.channel === "Mono" ? null : (
						<Badge size="sm" variant="light" data-testid={`transcript-segment-channel-${segment.seq}`}>
							{t(`pages.transcription.channel.${segment.channel}`)}
						</Badge>
					)}
					<Text size="sm" style={{ minWidth: 0 }}>
						{segment.text}
					</Text>
				</Group>
			))}
		</Stack>
	);
}
