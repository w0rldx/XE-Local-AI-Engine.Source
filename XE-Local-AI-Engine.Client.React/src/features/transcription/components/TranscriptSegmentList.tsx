import { Badge, Group, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { TranscriptChannel, TranscriptSegmentView } from "@/features/transcription/models/TranscriptionModels";

interface TranscriptSegmentListProps {
	readonly segments: readonly TranscriptSegmentView[];
}

interface TranscriptSegmentRowProps {
	readonly seq: number;
	readonly startMs: number;
	readonly endMs: number;
	readonly text: string;
	readonly channel: TranscriptChannel;
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
	return (
		<Stack gap="xs" data-testid="transcript-segment-list">
			{segments.map((segment) => (
				<TranscriptSegmentRow
					key={segment.id}
					seq={segment.seq}
					startMs={segment.startMs}
					endMs={segment.endMs}
					text={segment.text}
					channel={segment.channel}
				/>
			))}
		</Stack>
	);
}

/**
 * One committed row. Shared with the live panel, which has no row `id` to key by (a live row is identified by its
 * sequence) and no persisted channel spelling — so the row takes the fields it renders rather than a REST view-model.
 */
export function TranscriptSegmentRow({ seq, startMs, endMs, text, channel }: TranscriptSegmentRowProps) {
	const { t } = useTranslation();

	return (
		<Group gap="sm" align="flex-start" wrap="nowrap" data-testid={`transcript-segment-${seq}`}>
			<Text size="xs" c="dimmed" ff="monospace" style={{ flex: "0 0 auto" }}>
				{`${formatClipOffset(startMs)} – ${formatClipOffset(endMs)}`}
			</Text>
			{channel === "Mono" ? null : (
				<Badge size="sm" variant="light" data-testid={`transcript-segment-channel-${seq}`}>
					{t(`pages.transcription.channel.${channel}`)}
				</Badge>
			)}
			<Text size="sm" style={{ minWidth: 0 }}>
				{text}
			</Text>
		</Group>
	);
}
