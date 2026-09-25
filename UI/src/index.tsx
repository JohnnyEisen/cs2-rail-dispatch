// Rail Capacity Guard - HUD entry button.
// Visual parity with the game's own HUD buttons comes from using the native
// FloatingButton component (cs2/ui) instead of a hand-styled div.
import { ModRegistrar } from "cs2/modding";
import { FloatingButton } from "cs2/ui";
import * as React from "react";
import { Safe } from "./mods/safe";
import { RailGuardIcon } from "./railguard-icon";

// Icon-only native button: no text, no counter badge.
// A badge ("has advice") is intentionally NOT rendered yet - there is no data source
// until the P7 core produces TimetableAdvice; see the report for how it will be added.
const RailGuardButton = () => {
    const [selected, setSelected] = React.useState(false);
    return (
        <FloatingButton
            src={RailGuardIcon}
            tooltipLabel="Rail Capacity Guard"
            selected={selected}
            onSelect={() => setSelected((v) => !v)}
        />
    );
};

const register: ModRegistrar = (moduleRegistry) => {
    try {
        console.info("[RailCapacityGuard] register() running");
        moduleRegistry.append("GameTopLeft", () => (
            <Safe>
                <RailGuardButton />
            </Safe>
        ));
        console.info("[RailCapacityGuard] GameTopLeft append done");
    } catch (e) {
        console.error("[RailCapacityGuard] register failed: " + String(e));
    }
};

export default register;
