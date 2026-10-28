import { createFileRoute } from "@tanstack/react-router";

import { isTerminalAcquisitionPhase } from "@/core/model-downloads/models/GgufAcquisitionModels";
import { useActiveGgufDownloads } from "@/core/model-downloads/queries/useGgufDownload";
import { Chat } from "@/features/chat/pages/Chat";
import { AiAcknowledgementGate } from "@/features/onboarding/components/AiAcknowledgementGate";

// Chat is the landing page, so it also carries the first-run download progress. The feed is the models feature's
// hub-backed hook; the route wires it in so the chat feature does not import another feature. Chat shows it only
// while no chat model is usable.
function ChatRoute() {
	const downloads = useActiveGgufDownloads();
	const setupDownload = [...downloads.values()].find((download) => !isTerminalAcquisitionPhase(download.phase));
	return (
		<>
			<AiAcknowledgementGate />
			<Chat setupDownload={setupDownload} />
		</>
	);
}

export const Route = createFileRoute("/_layout/chat")({
	component: ChatRoute,
});
