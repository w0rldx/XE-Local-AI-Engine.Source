import { createElement } from "react";
import { useRouter } from "@tanstack/react-router";

import { AppErrorFallback } from "@/AppErrorFallback";
import type { RootErrorComponentProps } from "@/core/integrations/tanstack-router/Root.types";

export function RootErrorComponent({ error, reset }: RootErrorComponentProps) {
	const router = useRouter();

	return createElement(AppErrorFallback, {
		error,
		onRetry: () => {
			reset();
			router.invalidate();
		},
	});
}