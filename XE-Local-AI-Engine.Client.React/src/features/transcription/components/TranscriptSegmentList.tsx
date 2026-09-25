import { ActionIcon, Badge, Button, Group, Stack, Text, Textarea } from "@mantine/core";
import { IconPencil } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { toast } from "@/core/ui/notifications/Toast";
import {
	isSessionTranscribingRefusal,
	TRANSCRIPT_SEGMENT_TEXT_MAX_LENGTH,
	type TranscriptChannel,
	type TranscriptSegmentView,
} from "@/features/transcription/models/TranscriptionModels";
import { useUpdateTranscriptSegment } from "@/features/transcription/queries/useTranscriptionQueries";

interface TranscriptSegmentListProps {
	readonly segments: readonly TranscriptSegmentView[];
	/**
	 * The session the rows belong to, and whether their text may be edited. Editing is for finished sessions only
	 * (Completed, Failed, Cancelled): the page decides, the node refuses a transcribing one with a 409 regardless.
	 */
	readonly sessionId?: string;
	readonly editable?: boolean;
}

interface TranscriptSegmentRowProps {
	readonly seq: number;
	readonly startMs: number;
	readonly endMs: number;
	readonly text: string;
	readonly channel: TranscriptChannel;
	/** Renders the row's edit button when set. The live panel never passes it. */
	readonly onEdit?: () => void;
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
export function TranscriptSegmentList({ segments, sessionId, editable = false }: TranscriptSegmentListProps) {
	// One row edited at a time: a second editor would hold a draft the first save can silently discard.
	const [editingSeq, setEditingSeq] = useState<number | null>(null);
	const canEdit = editable && sessionId !== undefined;

	return (
		<Stack gap="xs" data-testid="transcript-segment-list">
			{segments.map((segment) =>
				canEdit && editingSeq === segment.seq ? (
					<TranscriptSegmentEditor key={segment.id} sessionId={sessionId} segment={segment} onDone={() => setEditingSeq(null)} />
				) : (
					<TranscriptSegmentRow
						key={segment.id}
						seq={segment.seq}
						startMs={segment.startMs}
						endMs={segment.endMs}
						text={segment.text}
						channel={segment.channel}
						onEdit={canEdit ? () => setEditingSeq(segment.seq) : undefined}
					/>
				),
			)}
		</Stack>
	);
}

interface TranscriptSegmentEditorProps {
	readonly sessionId: string;
	readonly segment: TranscriptSegmentView;
	readonly onDone: () => void;
}

/** Inline editor for one row's text. Ctrl/Cmd+Enter saves, Escape cancels; the timing and channel are not editable. */
function TranscriptSegmentEditor({ sessionId, segment, onDone }: TranscriptSegmentEditorProps) {
	const { t } = useTranslation();
	const update = useUpdateTranscriptSegment(sessionId);
	const [draft, setDraft] = useState(segment.text);
	const [blocked, setBlocked] = useState(false);
	const trimmed = draft.trim();
	const canSave =
		trimmed.length > 0 && trimmed.length <= TRANSCRIPT_SEGMENT_TEXT_MAX_LENGTH && trimmed !== segment.text && !update.isPending;

	const save = (): void => {
		if (!canSave) {
			return;
		}
		setBlocked(false);
		update.mutate(
			{ seq: segment.seq, text: trimmed },
			{
				onSuccess: () => {
					toast.success(t("pages.transcription.segment.saved"));
					onDone();
				},
				onError: (error) => {
					// A still-transcribing session is a wait, not a failure: the editor stays open with the draft intact.
					if (isSessionTranscribingRefusal(error)) {
						setBlocked(true);
						return;
					}
					toast.error(t("pages.transcription.segment.failed"));
				},
			},
		);
	};

	return (
		<Stack gap="xs" data-testid={`transcript-segment-editor-${segment.seq}`}>
			<Textarea
				value={draft}
				onChange={(event) => setDraft(event.currentTarget.value)}
				onKeyDown={(event) => {
					if (event.key === "Escape") {
						onDone();
					} else if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
						event.preventDefault();
						save();
					}
				}}
				autosize={true}
				minRows={2}
				autoFocus={true}
				aria-label={t("pages.transcription.segment.textLabel")}
				error={trimmed.length > TRANSCRIPT_SEGMENT_TEXT_MAX_LENGTH ? t("pages.transcription.segment.tooLong") : undefined}
				data-testid={`transcript-segment-input-${segment.seq}`}
			/>
			{blocked ? (
				<InlineErrorAlert
					message={t("pages.transcription.segment.transcribing")}
					variant="light"
					data-testid={`transcript-segment-blocked-${segment.seq}`}
				/>
			) : null}
			<Group gap="xs">
				<Button
					size="xs"
					onClick={save}
					disabled={!canSave}
					loading={update.isPending}
					data-testid={`transcript-segment-save-${segment.seq}`}
				>
					{t("pages.transcription.segment.save")}
				</Button>
				<Button size="xs" variant="default" onClick={onDone} data-testid={`transcript-segment-cancel-${segment.seq}`}>
					{t("pages.transcription.segment.cancel")}
				</Button>
			</Group>
		</Stack>
	);
}

/**
 * One committed row. Shared with the live panel, which has no row `id` to key by (a live row is identified by its
 * sequence) and no persisted channel spelling — so the row takes the fields it renders rather than a REST view-model.
 */
export function TranscriptSegmentRow({ seq, startMs, endMs, text, channel, onEdit }: TranscriptSegmentRowProps) {
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
			<Text size="sm" style={{ minWidth: 0, flex: "1 1 auto" }}>
				{text}
			</Text>
			{onEdit === undefined ? null : (
				<ActionIcon
					variant="subtle"
					size="sm"
					onClick={onEdit}
					aria-label={t("pages.transcription.segment.edit")}
					data-testid={`transcript-segment-edit-${seq}`}
				>
					<IconPencil size={14} />
				</ActionIcon>
			)}
		</Group>
	);
}
