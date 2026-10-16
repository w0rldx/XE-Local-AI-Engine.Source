import type { ReactNode } from "react";
import type { Root } from "react-dom/client";

import { BootLoadError } from "@/BootLoadError";
import { reloadForUserRetry } from "@/core/integrations/vite/PreloadErrorReload";

// Renders `app` once `ready` resolves; on a rejection renders the load-error screen, whose Retry reloads the document.
export function renderWhenI18nReady(root: Root, ready: Promise<void>, app: ReactNode): Promise<void> {
	return ready.then(
		() => root.render(app),
		() => root.render(<BootLoadError onRetry={reloadForUserRetry} />),
	);
}
