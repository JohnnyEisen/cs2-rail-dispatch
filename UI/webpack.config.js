// Minimal hello-world UI bundle for Rail Capacity Guard.
// Structure mirrors TransitTimetables/UI (MIT, Copyright (c) 2026 AmicusDeus) but with
// our own output directory so the build never depends on CSII_USERDATAPATH.
const path = require("path");

module.exports = {
    mode: "production",
    stats: "errors-warnings",
    entry: { RailCapacityGuard: "./src/index.tsx" },
    // The game loads this as an ES module from coui://ui-mods/<id>/<id>.js and calls its
    // default export (the ModRegistrar). Mirrors TransitTimetables/UI/webpack.config.js.
    experiments: {
        outputModule: true,
    },
    output: {
        path: path.resolve(__dirname, "build"),
        // 社区约定：PDX 缓存里 UI 模块的入口是 <ModId>.mjs（本机 17 个 .mjs ↔ 17 条 Registered UI Module）
        filename: "[name].mjs",
        library: { type: "module" },
        publicPath: "coui://ui-mods/",
    },
    // The game injects these as globals; keep them external so we do not bundle React.
    externalsType: "window",
    externals: {
        react: "React",
        "react-dom": "ReactDOM",
        "cs2/modding": "cs2/modding",
        "cs2/api": "cs2/api",
        "cs2/bindings": "cs2/bindings",
        "cs2/l10n": "cs2/l10n",
        "cs2/ui": "cs2/ui",
        "cs2/input": "cs2/input",
        "cs2/utils": "cs2/utils",
        "cohtml/cohtml": "cohtml/cohtml",
    },
    module: {
        rules: [
            { test: /\.tsx?$/, use: "ts-loader", exclude: /node_modules/ },
        ],
    },
    resolve: { extensions: [".tsx", ".ts", ".js"] },
};
