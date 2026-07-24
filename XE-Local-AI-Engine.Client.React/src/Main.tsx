import "@mantine/core/styles.css";
import "@mantine/charts/styles.css";
import "@mantine/notifications/styles.css";
import "virtual:uno.css";
import "./global.css";
import "./i18n";

import { domAnimation, LazyMotion } from "framer-motion";
import { StrictMode } from "react";
import ReactDOM from "react-dom/client";

import { App } from "@/App";
import { installCollectors, rootErrorHandlers } from "@/core/diagnostics/Diagnostics";
import { installAutoCapture } from "@/features/diagnostics/BuildSnapshot";

// Install always-on diagnostics collectors before the first render so early errors are captured,
// then subscribe auto-capture so a recorded error assembles + persists a snapshot.
installCollectors();
installAutoCapture();

const rootElement = document.querySelector("#root");
if (rootElement && !rootElement.innerHTML) {
	const root = ReactDOM.createRoot(rootElement, rootErrorHandlers);
	root.render(
		<StrictMode>
			<LazyMotion features={domAnimation}>
				<App />
			</LazyMotion>
		</StrictMode>,
	);
}
