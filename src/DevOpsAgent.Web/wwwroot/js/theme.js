(() => {
    const key = "dva-theme";

    function apply(theme) {
        document.documentElement.dataset.theme = theme;
        document.dispatchEvent(new CustomEvent("dva-theme-changed", { detail: { theme } }));
    }

    window.dvaTheme = {
        current() {
            return document.documentElement.dataset.theme || "light";
        },
        toggle() {
            const next = this.current() === "dark" ? "light" : "dark";
            try {
                localStorage.setItem(key, next);
            } catch {
                // Theme switching still works when storage is unavailable.
            }
            apply(next);
            return next;
        }
    };
})();
