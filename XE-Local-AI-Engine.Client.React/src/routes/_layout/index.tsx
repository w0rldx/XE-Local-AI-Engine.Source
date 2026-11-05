import { createFileRoute } from "@tanstack/react-router";

import { Home } from "@/features/home/pages/Home";

// Home is the landing page. Every "go home" target (capability gates, setup pages, the error fallback, the post-login
// default) points at `/`, so all of them land here.
export const Route = createFileRoute("/_layout/")({
	component: Home,
});
