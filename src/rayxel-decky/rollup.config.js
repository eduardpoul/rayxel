import deckyPlugin from "@decky/rollup";

// Decky's own preset: React, @decky/ui and the manifest come from the loader at run time,
// so the bundle carries only this plugin's code.
export default deckyPlugin({});
