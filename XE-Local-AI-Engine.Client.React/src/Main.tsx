import "@mantine/core/styles.css";
import "@mantine/charts/styles.css";
import "@mantine/notifications/styles.css";
import "virtual:uno.css";
import "./global.css";

import { domAnimation, LazyMotion } from "framer-motion";
import { StrictMode } from "react";
import ReactDOM from "react-dom/client";

import { App } from "@/App";
import { installCollectors, rootErrorHandlers } from "@/core/diagnostics/Diagnostics";
import { installAutoCapture } from "@/features/diagnostics/BuildSnapshot";

import { i18nReady } from "./i18n";

// Install always-on diagnostics collectors before the first render so early errors are captured,
// then subscribe auto-capture so a recorded error assembles + persists a snapshot.
installCollectors();
installAutoCapture();

const rootElement = document.querySelector("#root");
if (rootElement && !rootElement.innerHTML) {
	const root = ReactDOM.createRoot(rootElement, rootErrorHandlers);
	// Hold the first render until the detected locale is renderable. i18nReady is already resolved for the
	// statically-bundled fallback ("en"), so English sessions are unaffected; any other language waits on its
	// own chunk rather than painting English text and swapping it a frame later. It never rejects — a failed
	// chunk fetch renders against the English fallback.
	i18nReady
		.then(() => {
			root.render(
				<StrictMode>
					<LazyMotion features={domAnimation}>
						<App />
					</LazyMotion>
				</StrictMode>,
			);
		})
		.catch(() => undefined);
}
