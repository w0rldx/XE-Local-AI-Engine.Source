// Bootstrap entry point (plan §7.2): install the always-on global collectors ONCE.
//
// Wires the collectors that own a global resource: console patch, window error listeners, the
// `globalThis.fetch` wrapper, and the router subscription. The other collectors are wired at their
// own seams (axios in AxiosInstance.ts, signalr in NodeChatConnection.ts, query in Context.ts,
// zustand per opted-in store, react-root in Main.tsx / App.tsx).

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { installConsoleCollector } from "@/core/diagnostics/collectors/Console";
import { installFetchCollector } from "@/core/diagnostics/collectors/Network.fetch";
import { installRouterCollector } from "@/core/diagnostics/collectors/Router";
import { installWindowErrorCollectors } from "@/core/diagnostics/collectors/WindowErrors";
import { startRrwebRecording, stopRrwebRecording } from "@/core/diagnostics/RrwebRecorder";

let teardown: (() => void) | undefined;

/** Install the global diagnostics collectors. Idempotent; returns a teardown that removes them. */
export function installCollectors(): () => void {
	if (teardown) {
		return teardown;
	}

	const teardowns = [
		installWindowErrorCollectors(),
		installConsoleCollector(),
		installFetchCollector(),
		installRouterCollector(),
	];

	// rrweb DOM replay is Developer-Mode-only (plan §7.5). Start now if enabled and react to toggles.
	// The rrweb chunk is dynamically imported inside startRrwebRecording, so it stays out of the main
	// bundle and is never fetched unless Developer Mode is on.
	if (useDeveloperModeStore.getState().developerMode) {
		startRrwebRecording().catch(() => undefined);
	}
	const unsubscribeDevMode = useDeveloperModeStore.subscribe((state, previous) => {
		if (state.developerMode === previous.developerMode) {
			return;
		}
		if (state.developerMode) {
			startRrwebRecording().catch(() => undefined);
		} else {
			stopRrwebRecording();
		}
	});
	teardowns.push(() => {
		unsubscribeDevMode();
		stopRrwebRecording();
	});

	teardown = () => {
		for (const dispose of teardowns) {
			dispose();
		}
		teardown = undefined;
	};
	return teardown;
}
