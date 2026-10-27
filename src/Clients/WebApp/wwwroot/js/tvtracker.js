// Browser helpers called from WebPlatformServices.
window.tvtracker = {
    timeZone: () => Intl.DateTimeFormat().resolvedOptions().timeZone,

    // Device settings (the spoiler shield, the theme). Storage can be blocked (private windows), which reads as unset.
    getPreference: (key) => {
        try { return localStorage.getItem("tvtracker." + key); } catch { return null; }
    },
    setPreference: (key, value) => {
        try {
            if (value === null) localStorage.removeItem("tvtracker." + key);
            else localStorage.setItem("tvtracker." + key, value);
        } catch { }
    },

    downloadText: (fileName, text) => {
        const blob = new Blob([text], { type: "application/json" });
        const link = document.createElement("a");
        link.href = URL.createObjectURL(blob);
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(link.href), 1000);
    },

    // Asks for permission, registers the service worker and subscribes to push.
    // Returns { endpoint, p256dh, auth } or null if push isn't available or was refused.
    subscribePush: async (publicKey, appName) => {
        if (!("serviceWorker" in navigator) || !("PushManager" in window) || !("Notification" in window)) {
            return null;
        }

        const permission = await Notification.requestPermission();
        if (permission !== "granted") {
            return null;
        }

        const registration = await navigator.serviceWorker.register("/sw.js?app=" + encodeURIComponent(appName));
        await navigator.serviceWorker.ready;

        const existing = await registration.pushManager.getSubscription();
        const subscription = existing ?? await registration.pushManager.subscribe({
            userVisibleOnly: true,
            applicationServerKey: urlBase64ToUint8Array(publicKey)
        });

        const json = subscription.toJSON();
        return { endpoint: json.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth };
    }
};

function urlBase64ToUint8Array(base64) {
    const padding = "=".repeat((4 - (base64.length % 4)) % 4);
    const raw = atob((base64 + padding).replace(/-/g, "+").replace(/_/g, "/"));
    return Uint8Array.from([...raw].map(c => c.charCodeAt(0)));
}
