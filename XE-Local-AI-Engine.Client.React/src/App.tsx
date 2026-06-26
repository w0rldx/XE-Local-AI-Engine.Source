import { RouterProvider } from "@tanstack/react-router";
import dayjs from "dayjs";
import utc from "dayjs/plugin/utc";
import { ErrorBoundary } from "react-error-boundary";

import { AppErrorFallback } from "@/AppErrorFallback";
import { Provider as TanStackQueryProvider } from "@/core/integrations/tanstack-query/Provider";
import { router } from "@/core/integrations/tanstack-router/Router";
import { ThemeProvider } from "@/core/theme/provider/ThemeProvider";
import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { GgufDownloadPoller } from "@/features/models/components/GgufDownloadPoller";
import { OnboardingProvider } from "@/features/onboarding/components/OnboardingProvider";
import { ClientAiRuntimeProvider } from "@/features/voice/ClientAiRuntimeProvider";

export function App() {
	dayjs.extend(utc);

	return (
		<ThemeProvider>
			<TanStackQueryProvider>
				<ConfirmProvider>
					<GgufDownloadPoller />
					<ClientAiRuntimeProvider>
						<OnboardingProvider>
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
								onReset={() => {
									router.invalidate();
								}}
							>
								<RouterProvider router={router} />
							</ErrorBoundary>
						</OnboardingProvider>
					</ClientAiRuntimeProvider>
				</ConfirmProvider>
			</TanStackQueryProvider>
		</ThemeProvider>
	);
}
