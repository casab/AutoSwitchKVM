# autoswitchkvm.com

The AutoSwitch KVM website: one friendly page that explains what the app does, with an
interactive desk demo (press the KVM button, watch the trackpad hop over). Built with
[Eleventy](https://www.11ty.dev/) and served by Cloudflare Pages.

## Work on it

```sh
cd website
npm ci          # first time
npm start       # http://localhost:8080, rebuilds on save
npm run build   # writes _site/
```

## Deploy (Cloudflare Workers, static assets)

The site is a static-assets-only Worker (`wrangler.jsonc`: serves `_site`, `404.html` for
unknown paths; no Worker script). In the Cloudflare dashboard: **Workers & Pages ▸ Create ▸ Import
a repository**, pick this repo, and use:

| Setting | Value |
|---|---|
| Project name | `autoswitchkvm` (must match `name` in `wrangler.jsonc`) |
| Path (Advanced settings) | `/website` |
| Build command | `npm run build` |
| Deploy command | `npx wrangler deploy` |
| Preview command | `npx wrangler preview` (or `npx wrangler versions upload`) |

Then attach `autoswitchkvm.com` (and `www`, if you want it) under the Worker's **Settings ▸
Domains & Routes**. Pushes to `main` redeploy; with preview builds on, other branches get preview
URLs. `_redirects` (including the external GitHub download links) and `_headers` work the same as
on Pages. Locally, `npx wrangler deploy --dry-run` checks the config without uploading.

Cloudflare Pages works too, without `wrangler.jsonc`: root directory `website`, build command
`npm run build`, output directory `_site`.

## What's where

```
wrangler.jsonc          Cloudflare Workers config (static assets from _site)
src/index.njk           the home page (all sections)
src/404.njk             "This page switched to the other computer."
src/_includes/base.njk  layout: head/meta, header, footer
src/_data/site.js       names, links, download URLs
src/_redirects          /download/mac, /download/windows, /download/windows-arm64 -> GitHub Releases
src/_headers            security headers (strict CSP: everything is same-origin) and caching
src/assets/css, js      styles (light + dark) and the demo / OS-aware download button
src/assets/icon.png     the app icon (copied from windows/src/AutoSwitchKVM.App/Assets/app.png)
src/assets/og.png       social preview, made by tools/make-og.py
```

- **Screenshots** are not copied into this folder: `eleventy.config.js` takes them from
  `osx/docs/images/` and `windows/docs/images/`, so the site always shows the same images as the
  READMEs.
- **Fonts** are self-hosted (Nunito from `@fontsource-variable/nunito`), so the site makes no
  third-party requests and the CSP can stay `'self'`. That also means no inline `<script>` or
  `style="…"`: put styles in `site.css` and code in `site.js`.
- **Pending (next release):** from the first tag after v0.2.0, `macos-release.yml` also attaches
  `AutoSwitchKVM-macos.dmg` (fixed name). Once that release is out, point `/download/mac` in
  `src/_redirects` at `https://github.com/casab/AutoSwitchKVM/releases/latest/download/AutoSwitchKVM-macos.dmg`
  so the Mac button downloads directly. Until then it must stay on the release page (the file 404s).
- **Downloads** go through `/download/*` redirects. The Windows installers keep a fixed file name
  in every release, so they download directly. The macOS DMG's name includes the version, so
  `/download/mac` opens the latest release page.
- The demo respects `prefers-reduced-motion` and only autoplays while it's on screen, until the
  visitor presses the button.
