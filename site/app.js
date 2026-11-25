// Optional enhancements. The page works without this file: every download is listed with static links and text.
//   1. Show the visitor's own system first: the hero button and the download tabs.
//   2. Fill in versions and download sizes from GitHub, and point each button at the newest release that has its file.
(function () {
  var root = document.documentElement;
  root.classList.add("js");

  // ---- platform tabs ----
  var platforms = ["windows", "macos", "linux"];
  function detect() {
    var hint = ((navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || "") + " " + (navigator.userAgent || "");
    if (/android|iphone|ipad|ipod/i.test(hint)) return null;
    if (/mac/i.test(hint)) return "macos";
    if (/linux|x11|cros/i.test(hint)) return "linux";
    if (/win/i.test(hint)) return "windows";
    return null;
  }
  function select(platform, focus) {
    platforms.forEach(function (name) {
      var tab = document.getElementById("tab-" + name), panel = document.getElementById("panel-" + name);
      if (!tab || !panel) return;
      var on = name === platform;
      tab.setAttribute("aria-selected", on ? "true" : "false");
      tab.tabIndex = on ? 0 : -1;
      panel.hidden = !on;
      if (on && focus) tab.focus();
    });
  }
  platforms.forEach(function (name, index) {
    var tab = document.getElementById("tab-" + name);
    if (!tab) return;
    tab.addEventListener("click", function () { select(name); });
    tab.addEventListener("keydown", function (event) {
      var step = event.key === "ArrowRight" ? 1 : event.key === "ArrowLeft" ? -1 : 0;
      if (!step) return;
      event.preventDefault();
      select(platforms[(index + step + platforms.length) % platforms.length], true);
    });
  });
  // Links elsewhere on the page that mean "the download for this system".
  document.querySelectorAll("a[data-platform]").forEach(function (link) {
    link.addEventListener("click", function () { select(link.getAttribute("data-platform")); });
  });
  var mine = detect();
  select(mine || "windows");

  // ---- hero buttons: the visitor's own system is the highlighted one ----
  document.querySelectorAll(".os-buttons .os").forEach(function (button) {
    var on = button.getAttribute("data-os") === mine;
    button.classList.toggle("primary", on);
    button.classList.toggle("secondary", !on);
  });

  // ---- versions, sizes and newest links ----
  if (!window.fetch) return;
  var api = "https://api.github.com/repos/afj176/mason-agent/releases/";
  function release(path) {
    return fetch(api + path, { headers: { Accept: "application/vnd.github+json" } })
      .then(function (response) { return response.ok ? response.json() : null; })
      .catch(function () { return null; });
  }
  function versionOf(text) { var found = /(\d+)\.(\d+)\.(\d+)/.exec(text || ""); return found ? found[0] : null; }
  function atLeast(version, wanted) {
    var a = version.split(".").map(Number), b = wanted.split(".").map(Number);
    for (var i = 0; i < 3; i++) { if (a[i] !== b[i]) return a[i] > b[i]; }
    return true;
  }
  function setText(selector, text) { document.querySelectorAll(selector).forEach(function (el) { el.textContent = text; }); }
  function apply(found) {
    (found.assets || []).forEach(function (asset) {
      if (!(asset.size > 0) || typeof asset.browser_download_url !== "string") return;
      var megabytes = asset.size / 1048576;
      var size = megabytes >= 10 ? Math.round(megabytes) + " MB" : megabytes.toFixed(1) + " MB";
      document.querySelectorAll("[data-size]").forEach(function (el) {
        // The macOS and Linux buttons have no static size, so theirs follows the label after a separator.
        if (el.getAttribute("data-size") === asset.name) el.textContent = (/macos|linux/.test(asset.name) ? " · " : "") + size;
      });
      document.querySelectorAll("a[data-asset]").forEach(function (el) {
        if (el.getAttribute("data-asset") === asset.name && asset.browser_download_url.indexOf("https://github.com/afj176/mason-agent/releases/download/") === 0) el.href = asset.browser_download_url;
      });
    });
  }
  Promise.all([release("tags/preview"), release("latest")]).then(function (results) {
    var preview = results[0], latest = results[1];
    // The preview first, then the latest full release, so a full release that carries a file wins.
    if (preview) {
      apply(preview);
      var previewVersion = versionOf(preview.name);
      if (previewVersion) setText("[data-preview-version]", previewVersion);
    }
    if (latest && typeof latest.tag_name === "string") {
      var version = versionOf(latest.tag_name);
      apply(latest);
      if (version) {
        setText("[data-version]", version);
        if ((latest.assets || []).some(function (asset) { return asset.name === "MasonAgent-macos.pkg"; })) setText("[data-preview-version]", version);
        document.querySelectorAll("[data-before]").forEach(function (el) { el.hidden = atLeast(version, el.getAttribute("data-before")); });
        document.querySelectorAll("[data-from]").forEach(function (el) { el.hidden = !atLeast(version, el.getAttribute("data-from")); });
      }
    }
  });
})();
