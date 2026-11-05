import { createFileRoute } from "@tanstack/react-router";

import { isTerminalAcquisitionPhase } from "@/core/model-downloads/models/GgufAcquisitionModels";
import { useActiveGgufDownloads } from "@/core/model-downloads/queries/useGgufDownload";
import { Chat } from "@/features/chat/pages/Chat";

// Chat's empty state also carries the first-run download progress, as Home does. The feed is the shared hub-backed
// hook; the route wires it in so the page stays a plain prop consumer. Chat shows it only while no chat model is usable.
function ChatRoute() {
	const downloads = useActiveGgufDownloads();
	const setupDownload = [...downloads.values()].find((download) => !isTerminalAcquisitionPhase(download.phase));
	return <Chat setupDownload={setupDownload} />;
}

export const Route = createFileRoute("/_layout/chat")({
	component: ChatRoute,
});
