import { RouterProvider } from "@tanstack/react-router";
import dayjs from "dayjs";
import utc from "dayjs/plugin/utc";
import { ErrorBoundary } from "react-error-boundary";

import { Provider as TanStackQueryProvider } from "@/core/integrations/tanstack-query/Provider";
import { router } from "@/core/integrations/tanstack-router/Router";
import { ThemeProvider } from "@/core/theme/provider/ThemeProvider";
import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { OnboardingProvider } from "@/features/onboarding/components/OnboardingProvider";

import { AppErrorFallback } from "@/AppErrorFallback";

export function App() {
	dayjs.extend(utc);

	return (
		<ThemeProvider>
			<TanStackQueryProvider>
				<ConfirmProvider>
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
				</ConfirmProvider>
			</TanStackQueryProvider>
		</ThemeProvider>
	);
}
