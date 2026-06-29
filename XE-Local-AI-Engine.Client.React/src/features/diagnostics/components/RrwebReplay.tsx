// Lane C: rrweb DOM-replay viewer (plan §7.6) — Developer-Mode only.
//
// rrweb 2.0.1 DOES export `Replayer`, so we lazy-`import("rrweb")` it (keeping the library code-split
// out of the main bundle) and replay the snapshot's packed segment on demand. Events were packed by
// Lane D's `packRrwebEvent` ("v1" zlib format), so we unpack them with `unpackRrwebEvent` before
// handing them to the Replayer. If the Replayer cannot be initialised, we fall back to a "segment
// present" note so the captured data is still acknowledged. Rendered text is masked at capture
// (plan §3 / HIGH-3), so the replay shows layout and interactions only — never conversation content.

import { Alert, Button, Group, Stack, Text } from "@mantine/core";
import { IconInfoCircle, IconPlayerPlay } from "@tabler/icons-react";
import { useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import type { RrwebPackedEvent } from "@/core/diagnostics/Diagnostics";
import { unpackRrwebEvent } from "@/core/diagnostics/RrwebRecorder";

export interface RrwebReplayProps {
	readonly events: readonly RrwebPackedEvent[];
}

/** Minimal Replayer surface we rely on; typed locally to avoid an `any` from the dynamic import. */
interface ReplayerLike {
	play(): void;
}
type ReplayerConstructor = new (events: unknown[], options: { root: HTMLElement }) => ReplayerLike;

export function RrwebReplay({ events }: RrwebReplayProps) {
	const { t } = useTranslation();
	const containerRef = useRef<HTMLDivElement | null>(null);
	const [started, setStarted] = useState(false);
	const [failed, setFailed] = useState(false);

	const handlePlay = async (): Promise<void> => {
		const root = containerRef.current;
		if (!root) {
			return;
		}
		setStarted(true);
		try {
			const rrweb = (await import("rrweb")) as unknown as Record<string, unknown>;
			const ReplayerImpl = rrweb["Replayer"] as ReplayerConstructor;
			const unpacked = events.map((event) => unpackRrwebEvent(event));
			const replayer = new ReplayerImpl(unpacked, { root });
			replayer.play();
		} catch {
			setFailed(true);
		}
	};

	return (
		<Stack gap="sm">
			<Alert variant="light" color="gray" icon={<IconInfoCircle size={16} />}>
				{t("diagnostics.replay.masked")}
			</Alert>
			<Group justify="space-between">
				<Text size="sm">{t("diagnostics.replay.segmentPresent", { count: events.length })}</Text>
				<Button size="xs" variant="default" leftSection={<IconPlayerPlay size={14} />} disabled={started} onClick={handlePlay}>
					{t("diagnostics.replay.play")}
				</Button>
			</Group>
			{failed && (
				<Text c="dimmed" size="sm">
					{t("diagnostics.replay.unavailable")}
				</Text>
			)}
			<div ref={containerRef} style={{ minHeight: started && !failed ? 240 : 0, overflow: "hidden" }} />
		</Stack>
	);
}
