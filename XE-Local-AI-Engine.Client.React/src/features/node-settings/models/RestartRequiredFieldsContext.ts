import { createContext } from "react";

// The node's restart-gated fields, from the settings response's `restartRequiredFields`. The page provides it; a card
// rendered without a provider badges nothing.
export const RestartRequiredFieldsContext = createContext<ReadonlySet<string>>(new Set());
