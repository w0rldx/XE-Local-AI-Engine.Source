import { ScrollArea, Stack, Text, VisuallyHidden } from "@mantine/core";
import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { formatDurationCompact } from "@/core/formatting/TimeFormatting";
import { type LiveOutputLine, liveOutputLineCap } from "@/core/ui/components/LiveOutputView/LiveOutputLines";
import classes from "@/core/ui/components/LiveOutputView/LiveOutputView.module.css";

export interface LiveOutputViewProps {
	readonly lines: readonly LiveOutputLine[];
	/** Earlier lines the caller's buffer already dropped (see `appendLiveOutputLines`). */
	readonly hiddenLines?: number;
	/** Lines the server never delivered live. */
	readonly skippedLines?: number;
	/** Shows the elapsed and last-output clock while true. */
	readonly running: boolean;
	/** Accessible name of the log region. */
	readonly label: string;
	readonly testId?: string;
	/**
	 * When the command started and when its last line arrived (epoch ms), for a caller that records them as events
	 * arrive. Passing `startedAt` hands both times to the caller; without it the view stamps them itself, which only
	 * works while its effects run (a collapsed Mantine Collapse runs none).
	 */
	readonly startedAt?: number;
	readonly lastOutputAt?: number;
}

// Within this many pixels of the end counts as "at the bottom", so sub-pixel rounding never unpins the view.
const bottomSlackPx = 24;
const clockTickMs = 1000;

function useClock(active: boolean): number {
	const [now, setNow] = useState(() => Date.now());
	useEffect(() => {
		if (!active) {
			return;
		}
		setNow(Date.now());
		const handle = setInterval(() => setNow(Date.now()), clockTickMs);
		return () => clearInterval(handle);
	}, [active]);
	return now;
}

/**
 * Read-only view of one sandbox command's live output: text-only lines (never parsed as markup), stderr marked by
 * color and a border plus a screen-reader prefix, bounded height, and a view that follows new lines only while the
 * reader is already at the bottom. While running it shows how long the command has run and when output last arrived,
 * both from the browser clock.
 */
export function LiveOutputView({
	lines,
	hiddenLines = 0,
	skippedLines = 0,
	running,
	label,
	testId,
	startedAt: trackedStartedAt,
	lastOutputAt: trackedLastOutputAt,
}: LiveOutputViewProps) {
	const { t } = useTranslation();
	const viewportRef = useRef<HTMLDivElement>(null);
	const pinnedRef = useRef(true);
	const [mountedAt] = useState(() => Date.now());
	const [seenOutputAt, setSeenOutputAt] = useState<number | null>(null);
	const now = useClock(running);
	const startedAt = trackedStartedAt ?? mountedAt;
	const lastOutputAt = trackedStartedAt === undefined ? seenOutputAt : (trackedLastOutputAt ?? null);

	// The caller's buffer is capped already; this keeps the render bounded for a caller that is not.
	const overflow = Math.max(0, lines.length - liveOutputLineCap);
	const visible = overflow > 0 ? lines.slice(overflow) : lines;
	const hidden = hiddenLines + overflow;

	useEffect(() => {
		if (lines.length > 0) {
			setSeenOutputAt(Date.now());
		}
	}, [lines]);

	// Follow new output only when the reader was at the bottom; a reader scrolled up keeps their place.
	useLayoutEffect(() => {
		const viewport = viewportRef.current;
		if (viewport !== null && pinnedRef.current && lines.length > 0) {
			viewport.scrollTop = viewport.scrollHeight;
		}
	}, [lines]);

	const handleScroll = () => {
		const viewport = viewportRef.current;
		if (viewport !== null) {
			pinnedRef.current = viewport.scrollHeight - viewport.scrollTop - viewport.clientHeight <= bottomSlackPx;
		}
	};

	return (
		<Stack gap={4} data-testid={testId}>
			{running ? (
				<Text size="xs" c="dimmed" data-testid={testId ? `${testId}-clock` : undefined}>
					{lastOutputAt === null
						? t("components.liveOutput.runningNoOutput", {
								defaultValue: "Running for {{elapsed}}, no output yet",
								elapsed: formatDurationCompact(Math.max(0, now - startedAt)),
							})
						: t("components.liveOutput.running", {
								defaultValue: "Running for {{elapsed}}, last output {{ago}} ago",
								elapsed: formatDurationCompact(Math.max(0, now - startedAt)),
								ago: formatDurationCompact(Math.max(0, now - lastOutputAt)),
							})}
				</Text>
			) : null}
			{hidden > 0 ? (
				<Text size="xs" c="dimmed" data-testid={testId ? `${testId}-hidden` : undefined}>
					{t("components.liveOutput.hiddenLines", { count: hidden })}
				</Text>
			) : null}
			{skippedLines > 0 ? (
				<Text size="xs" c="dimmed" data-testid={testId ? `${testId}-skipped` : undefined}>
					{t("components.liveOutput.skippedLines", { count: skippedLines })}
				</Text>
			) : null}
			{/* role="log" with aria-live="off": a command can print many lines a second, and a polite live region would
			    queue every one of them for a screen reader. The region is named and focusable, so it is read on demand. */}
			<ScrollArea.Autosize
				mah={260}
				type="auto"
				viewportRef={viewportRef}
				onScrollPositionChange={handleScroll}
				viewportProps={{ role: "log", "aria-live": "off", "aria-label": label, tabIndex: 0 }}
			>
				<div className={classes["log"]}>
					{visible.length === 0 ? (
						<Text size="xs" c="dimmed" ff="monospace">
							{t("components.liveOutput.empty", "No output yet.")}
						</Text>
					) : (
						visible.map((line, index) => (
							<div
								// biome-ignore lint/suspicious/noArrayIndexKey: lines only append or reset, so the absolute index is stable.
								key={hidden + index}
								className={line.stream === "stderr" ? `${classes["line"]} ${classes["stderr"]}` : classes["line"]}
								data-stream={line.stream}
							>
								{line.stream === "stderr" ? (
									<VisuallyHidden>{t("components.liveOutput.stderrPrefix", "stderr: ")}</VisuallyHidden>
								) : null}
								{line.text}
							</div>
						))
					)}
				</div>
			</ScrollArea.Autosize>
		</Stack>
	);
}
