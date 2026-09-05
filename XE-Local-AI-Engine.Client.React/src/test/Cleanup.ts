// Vitest setup: unmount every React tree a test mounted, after that test.
//
// React Testing Library only registers its own `afterEach(cleanup)` when `globals: true` is set, and this suite
// runs without globals — so nothing unmounted anything. A mounted tree that outlives its test keeps its effects,
// its SignalR handlers and its TanStack Query observers alive, and those keep polling into the NEXT test's MSW
// handlers: a read count assertion then counts requests from a component the test never rendered, and a
// `screen.getBy…` can match a node left over from the previous test instead of the one under test.
//
// `afterEach` is imported explicitly rather than taken from a global for the same reason.

import { cleanup } from "@testing-library/react";
import { afterEach } from "vitest";

afterEach(cleanup);
