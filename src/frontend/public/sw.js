// Shows the assistant's notifications, and opens the chat they belong to when tapped.
self.addEventListener('push', event => {
  let message = {}
  try { message = event.data ? event.data.json() : {} } catch { message = { body: event.data ? event.data.text() : '' } }
  const url = typeof message.url === 'string' && message.url.startsWith('/') ? message.url : '/'
  event.waitUntil(self.registration.showNotification(message.title || 'Lucia', {
    body: message.body || '', icon: '/lucia.svg', data: { url }, tag: url, renotify: true,
  }))
})

self.addEventListener('notificationclick', event => {
  event.notification.close()
  const url = new URL(event.notification.data?.url || '/', self.location.origin).href
  event.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(async windows => {
    const open = windows.find(client => new URL(client.url).origin === self.location.origin)
    if (!open) return self.clients.openWindow(url)
    await open.focus()
    return open.navigate(url).catch(() => open)
  }))
})
