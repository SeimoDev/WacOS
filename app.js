// WacOS website: language switch (Chinese is the markup default).
(() => {
  const en = {
    s1: "Stay in the middle,", s2: "roam both sides.",
    sub: "macOS Spaces, Mission Control and Stage Manager, on Windows 11.",
    cta: "Download", req: "Windows 11 24H2 or later",
  };
  const zh = {};
  document.querySelectorAll("[data-i18n]").forEach(el => zh[el.dataset.i18n] = el.textContent);
  let lang = "zh";
  try { lang = localStorage.getItem("lang") || (navigator.language.startsWith("zh") ? "zh" : "en"); } catch {}
  const btn = document.getElementById("lang");
  function apply() {
    const t = lang === "en" ? en : zh;
    document.querySelectorAll("[data-i18n]").forEach(el => el.textContent = t[el.dataset.i18n]);
    document.documentElement.lang = lang === "en" ? "en" : "zh-CN";
    document.title = lang === "en" ? "WacOS — Stay in the middle, roam both sides." : "WacOS — 站中间，串两边";
    btn.textContent = lang === "en" ? "中文" : "EN";
  }
  btn.addEventListener("click", () => { lang = lang === "en" ? "zh" : "en"; try { localStorage.setItem("lang", lang); } catch {} apply(); });
  apply();
})();
