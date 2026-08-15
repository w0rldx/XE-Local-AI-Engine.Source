import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
	createMemoryHistory,
	createRootRoute,
	createRoute,
	createRouter,
	Outlet,
	RouterProvider,
} from "@tanstack/react-router";
import { render, type RenderResult } from "@testing-library/react";
import type { ReactElement, ReactNode } from "react";

import { installJsdomEnvironmentMocks } from "@/test/MantineTestRender";

// Initialises the app's own i18next instance as a side effect. `i18n.ts` calls `.use(initReactI18next)`, which
// registers the instance as react-i18next's default — so `useTranslation()` resolves against the real `en` bundle
// with no <I18nextProvider> in the tree. Consequence worth knowing before migrating a test onto this helper: a file
// that `vi.mock("react-i18next")`s the module wholesale must NOT use it, because the mock leaves `initReactI18next`
// undefined and `i18next.use(undefined)` throws at import time. Those files keep their own hand-rolled wrapper.
import "@/i18n";

export interface RenderWithProvidersOptions {
	/** Reuse a client across renders (e.g. to seed cache or assert invalidation). Defaults to a fresh one per render. */
	queryClient?: QueryClient;
	/**
	 * Wrap the tree in a TanStack Router with memory history. Required by anything reaching for router context —
	 * `useBlocker` (via `useUnsavedChangesGuard`), `useNavigate`, `<Link>`. Off by default: the router mounts
	 * asynchronously, so an opted-in test must `await` its first assertion (`findBy*`) rather than `getBy*`.
	 */
	withRouter?: boolean;
	/** Initial memory-history entry. Supplying it implies `withRouter`. */
	route?: string;
}

/**
 * A QueryClient with retries and caching disabled — the only sane defaults for a test. `retry: false` makes a
 * rejected query settle on the first attempt instead of after three exponential backoffs (which outlive the test),
 * and `gcTime: 0` stops one test's cache entry from surviving into the next.
 */
export function createTestQueryClient(): QueryClient {
	return new QueryClient({
		defaultOptions: {
			queries: { retry: false, gcTime: 0 },
			mutations: { retry: false },
		},
	});
}

// The app's real router is built from `src/routeTree.gen.ts` — every route module, and therefore every page, in one
// import. A unit test needs router *context*, not the app's routes, so this builds a throwaway two-route tree whose
// splat child renders the component under test at whatever path the test asked for.
function renderInMemoryRouter(ui: ReactNode, route: string): ReactElement {
	const rootRoute = createRootRoute({ component: Outlet });
	const splatRoute = createRoute({
		getParentRoute: () => rootRoute,
		path: "$",
		component: () => ui,
	});
	const indexRoute = createRoute({
		getParentRoute: () => rootRoute,
		path: "/",
		component: () => ui,
	});
	const router = createRouter({
		routeTree: rootRoute.addChildren([indexRoute, splatRoute]),
		history: createMemoryHistory({ initialEntries: [route] }),
	});

	// The test router is deliberately not the app's registered router type; `RouterProvider` is generic over it.
	// biome-ignore lint/suspicious/noExplicitAny: a locally-built route tree cannot satisfy the app's registered Router type.
	return <RouterProvider router={router as any} />;
}

/**
 * Renders `ui` inside the provider stack every non-trivial component in this app assumes: the jsdom stubs Mantine
 * needs (matchMedia / ResizeObserver), MantineProvider, a fresh QueryClient, the app's i18next instance, and — on
 * request — a memory-history TanStack Router.
 *
 * Returns Testing Library's usual result plus the `queryClient`, so a test can seed or assert cache state without
 * having to construct one itself.
 */
export function renderWithProviders(
	ui: ReactNode,
	options: RenderWithProvidersOptions = {},
): RenderResult & { queryClient: QueryClient } {
	installJsdomEnvironmentMocks();

	const queryClient = options.queryClient ?? createTestQueryClient();
	const withRouter = options.withRouter === true || options.route !== undefined;
	const content = withRouter ? renderInMemoryRouter(ui, options.route ?? "/") : ui;

	const result = render(
		<QueryClientProvider client={queryClient}>
			<MantineProvider>{content}</MantineProvider>
		</QueryClientProvider>,
	);

	return { ...result, queryClient };
}

/**
 * The same provider stack as a `wrapper` component, for `renderHook`. Kept as one implementation with
 * {@link renderWithProviders} so a hook test and a component test never disagree about what context exists.
 */
export function createProvidersWrapper(options: RenderWithProvidersOptions = {}): {
	wrapper: ({ children }: { children: ReactNode }) => ReactElement;
	queryClient: QueryClient;
} {
	installJsdomEnvironmentMocks();

	const queryClient = options.queryClient ?? createTestQueryClient();
	const withRouter = options.withRouter === true || options.route !== undefined;

	function Wrapper({ children }: { children: ReactNode }): ReactElement {
		return (
			<QueryClientProvider client={queryClient}>
				<MantineProvider>{withRouter ? renderInMemoryRouter(children, options.route ?? "/") : children}</MantineProvider>
			</QueryClientProvider>
		);
	}

	return { wrapper: Wrapper, queryClient };
}
