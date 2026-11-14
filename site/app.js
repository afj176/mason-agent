// Optional: show the latest release's version and download sizes.
// The page already carries static values, so any failure leaves it unchanged.
(function () {
  if (!window.fetch) return;
  var assets = { installer: "MasonAgent-Setup-x64.exe", portable: "MasonAgent-win-x64.zip" };
  fetch("https://api.github.com/repos/afj176/mason-agent/releases/latest", { headers: { Accept: "application/vnd.github+json" } })
    .then(function (response) { return response.ok ? response.json() : null; })
    .then(function (release) {
      if (!release || typeof release.tag_name !== "string") return;
      var version = release.tag_name.replace(/^v/i, "");
      if (/^\d+(\.\d+)*$/.test(version)) {
        document.querySelectorAll("[data-version]").forEach(function (el) { el.textContent = version; });
      }
      (release.assets || []).forEach(function (asset) {
        Object.keys(assets).forEach(function (key) {
          if (asset.name !== assets[key] || !(asset.size > 0)) return;
          var text = Math.round(asset.size / 1048576) + " MB";
          document.querySelectorAll('[data-size="' + key + '"]').forEach(function (el) { el.textContent = text; });
        });
      });
    })
    .catch(function () { /* Keep the static text. */ });
})();
