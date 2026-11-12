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

  return {
    dir: { input: "src", output: "_site", includes: "_includes", data: "_data" },
    htmlTemplateEngine: "njk",
    markdownTemplateEngine: "njk",
  };
}
