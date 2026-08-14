import { ModRegistrar } from "cs2/modding";
import { StationSuitabilityOverlay } from "mods/station-suitability-overlay";

const register: ModRegistrar = (moduleRegistry) => {

    moduleRegistry.append('GameTopRight', StationSuitabilityOverlay);
}

export default register;
