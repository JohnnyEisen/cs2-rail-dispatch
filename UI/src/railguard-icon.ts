// Our own icon art (32x32, silhouette style so it reads at HUD size).
// Deliberately inlined as a data URI: avoids depending on the emitted-asset path
// (coui://ui-mods/... + publicPath) which we have not verified for local mods.
const SVG =
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32">' +
    '<path fill-rule="evenodd" d="M8 4h16a3 3 0 0 1 3 3v13a3 3 0 0 1-3 3h-2l2 4h-3l-2-4h-6l-2 4H8l2-4H8a3 3 0 0 1-3-3V7a3 3 0 0 1 3-3zm1 4v6h14V8H9zm2.5 9a2 2 0 1 0 0 4 2 2 0 0 0 0-4zm9 0a2 2 0 1 0 0 4 2 2 0 0 0 0-4z"/>' +
    "</svg>";

export const RailGuardIcon = "data:image/svg+xml;utf8," + encodeURIComponent(SVG);
