import "@mantine/core/styles.css";
import "@mantine/notifications/styles.css";
import "virtual:uno.css";
import "./global.css";
import "./i18n";

import { domAnimation, LazyMotion } from "framer-motion";
import { StrictMode } from "react";
import ReactDOM from "react-dom/client";

import { App } from "@/App";

const rootElement = document.querySelector("#root");
if (rootElement && !rootElement.innerHTML) {
	const root = ReactDOM.createRoot(rootElement);
	root.render(
		<StrictMode>
			<LazyMotion features={domAnimation}>
				<App />
			</LazyMotion>
		</StrictMode>,
	);
}
