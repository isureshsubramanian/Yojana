// esbuild substitutes process.env.NODE_ENV at build time (web/build.mjs, web/dev.mjs); no Node types are needed.
declare const process: { env: { NODE_ENV: "production" | "development" } };
