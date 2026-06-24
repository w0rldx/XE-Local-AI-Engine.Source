import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { useActiveGgufDownloads } from "@/features/models/queries/useGgufDownload";

// Headless global reconciler mounted once in App.tsx (inside TanStackQueryProvider, inside the
// auth-gated region) so the GgufBrowseStore stays in sync with backend download state on every
// route — not just ModelManagement. Without this, `inFlightDownloads` set on download-start
// from ModelRecommendationsPage or the onboarding install step was never reconciled/removed
// unless the user happened to be on ModelManagement, causing "Downloading…" to stick forever.
// React Query dedupes the shared query key, so the ModelManagement mount costs no extra
// network requests. Auth gate mirrors useTourState.ts: query must not fire pre-login → 401.
export function GgufDownloadPoller() {
	const isAuthenticated = useNodeAuthStore((state) => Boolean(state.accessToken));
	useActiveGgufDownloads({ enabled: isAuthenticated });
	return null;
}
