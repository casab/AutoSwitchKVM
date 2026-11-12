// autoswitchkvm.com: download button for the visitor's OS, and the KVM desk demo.

(() => {
  // ---------- Put the visitor's platform first ----------
  const platform = (navigator.userAgentData?.platform || navigator.platform || navigator.userAgent || "").toLowerCase();
  const isWindows = platform.includes("win");
  document.querySelectorAll("[data-downloads]").forEach((group) => {
    const mac = group.querySelector('[data-os="mac"]');
    const win = group.querySelector('[data-os="windows"]');
    if (!mac || !win || !isWindows) return;
    win.classList.replace("btn--ghost", "btn--primary");
    mac.classList.replace("btn--primary", "btn--ghost");
    group.prepend(win);
  });

  // ---------- The desk demo ----------
  const demo = document.querySelector("[data-demo]");
  if (!demo) return;
  const kvm = demo.querySelector("[data-kvm]");
  const status = demo.querySelector("[data-status]");
  const buddies = demo.querySelectorAll(".buddy");
  const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const names = { mac: "Mac", pc: "PC" };
  let busy = false;
  let timers = [];
  let autoplay = null;

  const later = (ms, fn) => timers.push(setTimeout(fn, reduceMotion ? 0 : ms));

  function switchTo(target) {
    if (busy) return;
    busy = true;
    timers.forEach(clearTimeout);
    timers = [];
    const from = demo.dataset.active;

    kvm.classList.add("is-pressed");
    later(140, () => kvm.classList.remove("is-pressed"));
    demo.classList.remove("is-paired");
    status.textContent = `The ${names[from]} lets go of the trackpad and keyboard…`;

    later(450, () => {
      buddies.forEach((b) => {
        b.classList.remove("is-hopping");
        void b.offsetWidth; // restart the hop animation
        b.classList.add("is-hopping");
      });
      demo.dataset.active = target;
      status.textContent = `…and they hop over to the ${names[target]}…`;
    });

    later(1750, () => {
      demo.classList.add("is-paired");
      status.textContent = `Paired! They work with the ${names[target]} now.`;
      busy = false;
    });
    later(3200, () => demo.classList.remove("is-paired"));
  }

  const toggle = () => switchTo(demo.dataset.active === "mac" ? "pc" : "mac");

  kvm.addEventListener("click", () => {
    stopAutoplay();
    toggle();
  });

  // Gentle autoplay while the demo is on screen, until the visitor presses the button themselves.
  function startAutoplay() {
    if (reduceMotion || autoplay) return;
    autoplay = setInterval(toggle, 5200);
  }
  function stopAutoplay() {
    clearInterval(autoplay);
    autoplay = null;
    demo.dataset.touched = "true";
  }
  if ("IntersectionObserver" in window) {
    new IntersectionObserver(([entry]) => {
      if (demo.dataset.touched) return;
      if (entry.isIntersecting) startAutoplay();
      else {
        clearInterval(autoplay);
        autoplay = null;
      }
    }, { threshold: 0.4 }).observe(demo);
  }
})();
