# Browser companion (MVP)

Load this directory unpacked in `edge://extensions` and/or `chrome://extensions` with Developer mode. The desktop app must be running. The browser host never starts the monitor itself.

Build/publish EntLimit.NativeHost together with EntLimit.Core. Register `com.entlimit.bridge` separately for each browser using the installation script supplied with the project. Native host manifest must have this shape (use actual absolute published executable path and actual extension IDs):

```json
{
  "name": "com.entlimit.bridge",
  "description": "EntLimit local bridge",
  "path": "C:\\Program Files\\EntLimit\\EntLimit.NativeHost.exe",
  "type": "stdio",
  "allowed_origins": ["chrome-extension://ACTUAL_CHROME_ID/", "chrome-extension://ACTUAL_EDGE_ID/"]
}
```

Both origins must be exact IDs, no wildcards. Published store IDs can differ from development IDs. Registry default values must point to the absolute native host manifest JSON path:

From the project directory, register the unpacked IDs explicitly:

```powershell
.\scripts\register-browser-host.ps1 -ChromeExtensionId 'ACTUAL_CHROME_ID' -EdgeExtensionId 'ACTUAL_EDGE_ID'
```

- `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.entlimit.bridge`
- `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.entlimit.bridge`

The application uses per-user registration; administrative rights are unnecessary. Registration and extension installation are explicit user steps. Native transport is 4-byte little-endian UTF-8 byte length plus JSON. Accepted commands: `snapshot`, `open_learning`, `use`. Responses project only domains and shared session phase/deadline, never question history or browsing history.

Add domains in the desktop application. Open the extension popup, synchronize, then click **允许管理已选择的网站** to grant only these domain permissions. The extension does not request all-site access on install. New domains require another explicit grant. Removing a domain stops rules and future content script injection on next sync; existing page scripts disappear when that page closes. Previously granted browser permissions remain until manually removed.

Sync occurs on startup, popup/gate refresh, first managed-page use, and a one-minute alarm. There is no persistent native port or URL polling. Opening a managed page activates a Ready permit via the monitor. Background opening also counts as use in this MVP. A maximum approximately one-minute delay can apply to desktop configuration changes or expiry. Existing content stays available at expiry; new main-frame navigation is gated. No automatic video-end, single-page application route enforcement, forced closure, browser-wide restriction, notifications, or focus stealing. In-page reminders are best effort and may not appear over browser video fullscreen. Cached DNR rules survive a monitor outage; existing pages remain available. Open the desktop application and refresh after reconnecting.

The gate deliberately does not persist or restore original URLs. After learning, click refresh and reopen the intended site. No browsing history, page content, titles, cookies, or full URLs are sent to the host or written to local storage. Only configured domains, derived permissions, local phase/deadline, last sync time and diagnostic text are cached.

Disable/remove the extension in each browser to stop website rules. Remove the two per-user registry keys above and native host manifest to unregister the bridge. The extension is cooperative personal support: profiles without the extension, disabled extensions, incognito without explicit permission, other browsers and already loaded/cached content are outside the guarantee.

Official API references: [Chrome Native Messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging), [Edge Native Messaging](https://learn.microsoft.com/en-us/microsoft-edge/extensions/developer-guide/native-messaging), [Declarative Net Request](https://developer.chrome.com/docs/extensions/reference/api/declarativeNetRequest).
