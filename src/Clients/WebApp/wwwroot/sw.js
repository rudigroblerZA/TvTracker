// Service worker for browser push notifications from Notifications.API.
// Payload: { title, body, link }

self.addEventListener("push", event => {
    const data = event.data ? event.data.json() : {};
    event.waitUntil(
        self.registration.showNotification(data.title || "TV Tracker", {
            body: data.body || "",
            data: { link: data.link || "/" },
            tag: data.link || undefined
        })
    );
});

self.addEventListener("notificationclick", event => {
    event.notification.close();
    const link = event.notification.data?.link || "/";
    event.waitUntil(
        self.clients.matchAll({ type: "window", includeUncontrolled: true }).then(windows => {
            for (const client of windows) {
                if ("focus" in client) {
                    client.navigate(link);
                    return client.focus();
                }
            }
            return self.clients.openWindow(link);
        })
    );
});
