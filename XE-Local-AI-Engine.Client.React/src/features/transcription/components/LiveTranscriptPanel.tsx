import { Group, ScrollArea, Stack, Switch, Text } from "@mantine/core";
import { useRef } from "react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { useStickToBottomScroll } from "@/core/ui/hooks/useStickToBottomScroll";
import { TranscriptSegmentRow } from "@/features/transcription/components/TranscriptSegmentList";
import type { CaptureChannel } from "@/features/transcription/capture/CaptureSource";
import type { LiveTranscriptView } from "@/features/transcription/hooks/useTranscriptionHub";
import { toDisplayChannel } from "@/features/transcription/models/TranscriptionLiveModels";
import { useTranscriptionCaptureStore } from "@/features/transcription/stores/TranscriptionCaptureStore";

interface LiveTranscriptPanelProps {
	readonly view: LiveTranscriptView | null;
}

// The committed list scrolls inside this height instead of growing the page; a long session stays readable and the
// capture controls above it stay in view.
const LIVE_TRANSCRIPT_MAX_HEIGHT_PX = 420;

const partialChannels: readonly CaptureChannel[] = ["mono", "you", "others"];

/**
 * The live transcript: the committed rows the hub has delivered, plus one provisional line per lane that currently has
 * one.
 *
 * The provisional line is deliberately outside the committed list and visually distinct. Provisional text is replaced
 * wholesale on every update and can disagree with what is finally committed, so a reader who cannot tell the two apart
 * would quote text the transcript never kept. The committed rows use the same row component the finished, REST-fed
 * transcript uses, so a session does not change appearance the moment it ends.
 */
export function LiveTranscriptPanel({ view }: LiveTranscriptPanelProps) {
	const { t } = useTranslation();
	const showPartials = useTranscriptionCaptureStore((state) => state.showPartials);
	const setShowPartials = useTranscriptionCaptureStore((state) => state.actions.setShowPartials);

	const committed = view?.committed ?? [];
	const partials = view?.partials ?? {};
	const sourceQuietMs = view?.sourceQuietMs ?? null;

	// Follows new commits only while the reader is near the bottom; scrolling up to re-read is left alone.
	const viewportRef = useRef<HTMLDivElement | null>(null);
	const endRef = useRef<HTMLDivElement | null>(null);
	useStickToBottomScroll({
		viewportRef,
		endRef,
		virtualTotalSize: 0,
		scrollKey: `${committed.length}:${view?.lastSeq ?? 0}`,
		isStreamingActive: true,
	});

	return (
		<Stack gap="sm" data-testid="transcription-live-panel">
			<Group justify="space-between" align="center">
				<Text size="sm" c="dimmed">
					{t("pages.transcription.live.hint")}
				</Text>
				<Switch
					checked={showPartials}
					onChange={(event) => setShowPartials(event.currentTarget.checked)}
					label={t("pages.transcription.live.showPartials")}
					data-testid="transcription-show-partials"
				/>
			</Group>

			{/* The scroll box stays mounted while empty: the stick-to-bottom hook attaches its scroll listener to the
			    viewport once, on first commit, so a viewport that only appears with the first row would never get it. */}
			<ScrollArea.Autosize
				mah={LIVE_TRANSCRIPT_MAX_HEIGHT_PX}
				type="auto"
				viewportRef={viewportRef}
				data-testid="transcription-committed-scroll"
			>
				{committed.length === 0 ? (
					<EmptyState message={t("pages.transcription.live.waiting")} data-testid="transcription-live-empty" />
				) : (
					<Stack gap="xs" data-testid="transcription-committed-list">
						{committed.map((segment) => (
							<TranscriptSegmentRow
								key={segment.seq}
								seq={segment.seq}
								startMs={segment.startMs}
								endMs={segment.endMs}
								text={segment.text}
								channel={toDisplayChannel(segment.channel)}
							/>
						))}
						<div ref={endRef} />
					</Stack>
				)}
			</ScrollArea.Autosize>

			{sourceQuietMs === null ? null : (
				<Text size="sm" c="orange" role="status" data-testid="transcription-source-quiet">
					{t("pages.transcription.live.sourceQuiet", { seconds: Math.floor(sourceQuietMs / 1000) })}
				</Text>
			)}

			{!showPartials
				? null
				: partialChannels.map((channel) => {
						const text = partials[channel];
						return text === undefined || text.length === 0 ? null : (
							<Text key={channel} size="sm" fs="italic" c="dimmed" data-testid={`transcription-partial-${channel}`}>
								{channel === "mono"
									? text
									: t("pages.transcription.live.partialAttributed", {
											channel: t(`pages.transcription.channel.${toDisplayChannel(channel)}`),
											text,
										})}
							</Text>
						);
					})}
		</Stack>
	);
}
