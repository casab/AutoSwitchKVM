import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";

// Eleventy config for autoswitchkvm.com (Cloudflare Pages: root "website", build "npm run build",
// output "_site"). Screenshots come straight from the platform docs so the site never goes stale.
export default function (eleventyConfig) {
  eleventyConfig.addPassthroughCopy({ "src/assets": "assets" });
  eleventyConfig.addPassthroughCopy({ "src/_headers": "_headers", "src/_redirects": "_redirects" });

  // Self-hosted font (no third-party requests): Nunito variable, Latin + Latin Extended.
  eleventyConfig.addPassthroughCopy({
    "node_modules/@fontsource-variable/nunito/files/nunito-latin-wght-normal.woff2": "assets/fonts/nunito-latin.woff2",
    "node_modules/@fontsource-variable/nunito/files/nunito-latin-ext-wght-normal.woff2": "assets/fonts/nunito-latin-ext.woff2",
  });

  // App screenshots, shared with the READMEs.
  eleventyConfig.addPassthroughCopy({
    "../osx/docs/images/menu-bar-panel.png": "assets/img/mac-panel.png",
    "../windows/docs/images/tray-panel.png": "assets/img/windows-panel.png",
  });

  eleventyConfig.addFilter("year", () => new Date().getFullYear());

  // Cache busting: "/assets/css/site.css" | v  ->  "/assets/css/site.css?v=<content hash>".
  // Assets are cached for a day (src/_headers) while HTML is always revalidated, so without
  // this a returning visitor got the new page with yesterday's CSS and JS (2026-11-16).
  eleventyConfig.addFilter("v", (url) => {
    const file = path.join("src", url);
    const hash = crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex").slice(0, 10);
    return `${url}?v=${hash}`;
  });

  return {
    dir: { input: "src", output: "_site", includes: "_includes", data: "_data" },
    htmlTemplateEngine: "njk",
    markdownTemplateEngine: "njk",
  };
}
