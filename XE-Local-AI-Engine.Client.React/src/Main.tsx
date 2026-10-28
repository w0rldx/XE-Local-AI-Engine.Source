import "@mantine/core/styles.css";
import "@mantine/charts/styles.css";
import "@mantine/notifications/styles.css";
import "virtual:uno.css";
import "./global.css";

import { domAnimation, LazyMotion, MotionConfig } from "framer-motion";
import { StrictMode } from "react";
import ReactDOM from "react-dom/client";

import { App } from "@/App";
import { installCollectors, rootErrorHandlers } from "@/core/diagnostics/Diagnostics";
import { installPreloadErrorReload } from "@/core/integrations/vite/PreloadErrorReload";
import { installAutoCapture } from "@/features/diagnostics/models/BuildSnapshot";

import { renderWhenI18nReady } from "./BootI18n";
import { i18nReady } from "./i18n";

// Install always-on diagnostics collectors before the first render so early errors are captured,
// then subscribe auto-capture so a recorded error assembles + persists a snapshot.
installCollectors();
installAutoCapture();
installPreloadErrorReload();
if (import.meta.env.DEV) {
	import("@/core/accessibility/DevelopmentAccessibilityAudit")
		.then((accessibility) => accessibility.installDevelopmentAccessibilityAudit())
		.catch((error: unknown) => {
			console.error("[development feedback] Accessibility audit failed to load.", error);
		});
	import("@/core/performance/DevelopmentPerformanceFeedback")
		.then((performance) => performance.installDevelopmentPerformanceFeedback())
		.catch((error: unknown) => {
			console.error("[development feedback] Performance observer failed to load.", error);
		});
}

const rootElement = document.querySelector("#root");
if (rootElement && !rootElement.innerHTML) {
	const root = ReactDOM.createRoot(rootElement, rootErrorHandlers);
	// Hold the first render until the fallback ("en") and the detected locale are renderable: both are lazy chunks,
	// so painting earlier would show raw keys, or English text swapped a frame later. If `en` failed to load, an
	// untranslated load-error screen is shown instead of an app full of raw keys; its Retry reloads the document.
	renderWhenI18nReady(
		root,
		i18nReady,
		<StrictMode>
			<LazyMotion features={domAnimation}>
				{/* "user" honors the OS `prefers-reduced-motion` setting: transform and layout animations
				    (the sidebar collapse, the mobile drawer, the content-area reflow) snap to their end
				    state, while opacity still crossfades, so nothing becomes invisible. */}
				<MotionConfig reducedMotion="user">
					<App />
				</MotionConfig>
			</LazyMotion>
		</StrictMode>,
	).catch(() => undefined);
}
