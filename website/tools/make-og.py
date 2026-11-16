"""Build the social preview images from the site's own hero (same font, colors and KVM demo):

    src/assets/og.png               1200x630, the og:image every page links to
    tools/github-social-preview.png 1280x640, upload by hand in GitHub: repo Settings > General >
                                    Social preview (GitHub has no API for it)

Re-run after changing the hero copy or the demo:
    cd website && npm run build && python tools/make-og.py
Needs Python Playwright with Chromium (pip install playwright && playwright install chromium).
"""
import functools, http.server, os, socketserver, threading
from playwright.sync_api import sync_playwright

HERE = os.path.dirname(os.path.abspath(__file__))
SITE = os.path.join(HERE, '..', '_site')
OUTPUTS = [
    (os.path.join(HERE, '..', 'src', 'assets', 'og.png'), 1200, 630),
    (os.path.join(HERE, 'github-social-preview.png'), 1280, 640),
]

# Swap the page for a one-screen card: brand + headline on the left, the hero's KVM demo
# (cloned from the real markup) on the right, with the devices just arrived at the PC.
COMPOSE = r"""
(zoom) => {
  // Laid out at 1280 px wide, then zoomed to the output width so every size looks the same.
  document.documentElement.style.zoom = zoom;
  document.documentElement.style.setProperty('--og-h', (innerHeight / zoom) + 'px');
  const demo = document.querySelector('[data-demo]').cloneNode(true);
  demo.querySelector('figcaption')?.remove();
  demo.dataset.active = 'pc';
  demo.classList.add('is-paired');
  document.body.innerHTML = `
    <div class="og">
      <div class="og__copy">
        <div class="og__brand"><img src="/assets/icon.png" alt=""><span>AutoSwitch KVM</span></div>
        <h1><span class="nowrap">Press the KVM button.</span><br><span class="hl nowrap">Your devices follow.</span></h1>
        <p class="og__lead">Your Bluetooth keyboard, mouse, trackpad and headphones move with your KVM switch.</p>
        <p class="og__foot">Free for macOS and Windows · autoswitchkvm.com</p>
      </div>
      <div class="og__demo"></div>
    </div>`;
  document.querySelector('.og__demo').append(demo);
  const css = document.createElement('style');
  css.textContent = `
    html, body { margin: 0; height: 100%; overflow: hidden; background: var(--bg); }
    .og { position: relative; box-sizing: border-box; width: 1280px; height: var(--og-h); display: grid;
          grid-template-columns: 1fr auto; align-items: center; gap: 40px; padding: 0 56px 0 64px; overflow: hidden; isolation: isolate; }
    .og::before, .og::after { content: ""; position: absolute; border-radius: 50%; z-index: -1; }
    .og::before { width: 520px; height: 520px; right: -170px; top: -230px; background: var(--blue-soft); }
    .og::after { width: 380px; height: 380px; left: -150px; bottom: -220px; background: var(--coral-soft); }
    .og__brand { display: flex; align-items: center; gap: 16px; font-weight: 900; font-size: 34px; color: var(--ink); }
    .og__brand img { width: 64px; height: 64px; }
    .og h1 { font-size: 58px; line-height: 1.08; margin: 34px 0 22px; letter-spacing: -.01em; }
    .og__lead { font-size: 25px; line-height: 1.38; color: var(--ink-soft); font-weight: 700; margin: 0 0 30px; max-width: 32ch; }
    .og__foot { font-size: 21px; font-weight: 800; color: var(--ink-soft); margin: 0; }
    .og__demo { width: 540px; }
    .og__demo .desk { font-size: 15px; }
  `;
  document.head.append(css);
}
"""


def serve(root):
    class Quiet(http.server.SimpleHTTPRequestHandler):
        def log_message(self, *args):
            pass
    handler = functools.partial(Quiet, directory=root)
    httpd = socketserver.TCPServer(('127.0.0.1', 0), handler)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    return httpd


def main():
    if not os.path.exists(os.path.join(SITE, 'index.html')):
        raise SystemExit('Build the site first: npm run build')
    httpd = serve(SITE)
    url = f'http://127.0.0.1:{httpd.server_address[1]}/'
    with sync_playwright() as p:
        browser = p.chromium.launch()
        for out, w, h in OUTPUTS:
            page = browser.new_page(viewport={'width': w, 'height': h}, color_scheme='light',
                                    reduced_motion='reduce')
            page.goto(url, wait_until='networkidle')
            page.evaluate(COMPOSE, w / 1280)
            page.wait_for_load_state('networkidle')
            page.evaluate('document.fonts.ready')
            page.wait_for_timeout(300)
            page.screenshot(path=out)
            page.close()
            print('wrote', os.path.normpath(out), f'{w}x{h}')
        browser.close()
    httpd.shutdown()


if __name__ == '__main__':
    main()
