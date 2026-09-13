import { RouterProvider } from "@tanstack/react-router";
import { ErrorBoundary } from "react-error-boundary";

import { AppErrorFallback } from "@/AppErrorFallback";
import { onAppError } from "@/core/diagnostics/Diagnostics";
import { Provider as TanStackQueryProvider } from "@/core/integrations/tanstack-query/Provider";
import { router } from "@/core/integrations/tanstack-router/Router";
import { ThemeProvider } from "@/core/theme/provider/ThemeProvider";
import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { GgufDownloadPoller } from "@/features/models/components/GgufDownloadPoller";
import { ClientAiRuntimeProvider } from "@/features/voice/ClientAiRuntimeProvider";

export function App() {
	return (
		<ThemeProvider>
			<TanStackQueryProvider>
				<ConfirmProvider>
					<GgufDownloadPoller />
					<ClientAiRuntimeProvider>
						<ErrorBoundary
							fallbackRender={({ error, resetErrorBoundary }) => (
								<AppErrorFallback
									error={error}
									onRetry={() => {
										resetErrorBoundary();
										router.invalidate();
									}}
								/>
							)}
							onError={onAppError}
							onReset={() => {
								router.invalidate();
							}}
						>
							<RouterProvider router={router} />
						</ErrorBoundary>
					</ClientAiRuntimeProvider>
				</ConfirmProvider>
			</TanStackQueryProvider>
		</ThemeProvider>
	);
}
