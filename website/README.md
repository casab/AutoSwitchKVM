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

## Deploy (Cloudflare Pages)

Connect the GitHub repo in Cloudflare Pages with:

| Setting | Value |
|---|---|
| Production branch | `main` |
| Root directory | `website` |
| Build command | `npm run build` |
| Build output directory | `_site` |
| Environment variable (optional) | `NODE_VERSION` = `22` (Eleventy 3 needs Node 18 or newer) |

Then add `autoswitchkvm.com` (and `www`, if you want it) under **Custom domains**. Every push to
`main` that touches the site redeploys it; other branches get preview URLs.

## What's where

```
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
- **Downloads** go through `/download/*` redirects. The Windows installers keep a fixed file name
  in every release, so they download directly. The macOS DMG's name includes the version, so
  `/download/mac` opens the latest release page.
- The demo respects `prefers-reduced-motion` and only autoplays while it's on screen, until the
  visitor presses the button.
