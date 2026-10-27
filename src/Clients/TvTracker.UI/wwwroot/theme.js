// The theme picked in Settings ("light" or "dark"; unset follows the device), as data-theme on <html>. Loaded in the
// page head, before the stylesheet, so a saved choice applies before the first paint instead of flashing the other one.
// The choice is also kept in localStorage under the web app's preference key, so the phone gets it early too.
window.tvtrackerTheme = {
    apply: (theme) => {
        const root = document.documentElement;
        if (theme === "light" || theme === "dark") root.dataset.theme = theme;
        else delete root.dataset.theme;
        try {
            if (theme === "light" || theme === "dark") localStorage.setItem("tvtracker.theme", theme);
            else localStorage.removeItem("tvtracker.theme");
        } catch { }
    }
};

(() => {
    try {
        const theme = localStorage.getItem("tvtracker.theme");
        if (theme === "light" || theme === "dark") document.documentElement.dataset.theme = theme;
    } catch { }
})();
