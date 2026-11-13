// The client-side buffer behind LiveOutputView: one capped list of command output lines that the chat stream reducer
// and the Development live panel both append to, so the two surfaces drop the same lines the same way.

/** One redacted line of a sandbox command's output. The server already stripped ANSI and redacted it. */
export interface LiveOutputLine {
	stream: "stdout" | "stderr";
	text: string;
}

/** A capped line list plus how many earlier lines the cap dropped. */
export interface LiveOutputBuffer {
	lines: LiveOutputLine[];
	hiddenLines: number;
}

/** How many lines the browser keeps per command; older lines are dropped and counted, never rendered. */
export const liveOutputLineCap = 2000;

/** Maps a wire stream name to the two kinds the view distinguishes; anything but "stderr" renders as stdout. */
export function toLiveOutputStream(stream: string | null | undefined): LiveOutputLine["stream"] {
	return stream?.toLowerCase() === "stderr" ? "stderr" : "stdout";
}

/**
 * Appends (or, on `replace`, substitutes) incoming lines and trims the result to {@link liveOutputLineCap}, counting
 * the trimmed lines into `hiddenLines`. A replace starts the hidden count over: it re-states the command's output.
 */
export function appendLiveOutputLines(
	buffer: LiveOutputBuffer | undefined,
	incoming: readonly LiveOutputLine[],
	replace: boolean,
): LiveOutputBuffer {
	const base = replace || buffer === undefined ? { lines: [], hiddenLines: 0 } : buffer;
	if (incoming.length === 0 && !replace && buffer !== undefined) {
		return buffer;
	}

	const combined = [...base.lines, ...incoming];
	const overflow = Math.max(0, combined.length - liveOutputLineCap);
	return {
		lines: overflow > 0 ? combined.slice(overflow) : combined,
		hiddenLines: base.hiddenLines + overflow,
	};
}
