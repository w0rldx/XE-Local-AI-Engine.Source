import { createFileRoute } from "@tanstack/react-router";

import { CommandsPage } from "@/features/commands/pages/CommandsPage";

export const Route = createFileRoute("/_layout/commands")({ component: CommandsPage });
