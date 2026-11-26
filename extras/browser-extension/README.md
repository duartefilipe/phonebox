# PhoneBox video crop (browser extension)

Optional. With it, the **Now playing screen** widget's live video shows **only the video** that is playing in the
browser — not the tabs, the address bar or the rest of the page. Without it the agent still crops, to the part of the
window that keeps moving, which works well for most players but can take a second or two to settle and can include an
animated banner next to the video.

## What it does

A content script looks at each page for the video that is playing (the largest one on screen, inside iframes too, such
as an embedded YouTube player) and works out where its picture is, without the letterbox bars. The page's title and
that rectangle go to the PhoneBox agent on **this computer** (`http://127.0.0.1:8765`), about twice a second while a
video is on screen. The agent matches the title to the browser window it is capturing and crops to the rectangle.

Nothing goes anywhere else: no server, no analytics, no other site. The agent keeps the reports in memory only, forgets
them after 3 seconds, and only accepts them from a browser extension on this computer.

## Install (unpacked)

- **Chrome, Edge, Brave, Opera, Vivaldi**: open `chrome://extensions` (or `edge://extensions`), turn on
  **Developer mode**, click **Load unpacked** and choose this folder.
- **Firefox** (121 or later): open `about:debugging#/runtime/this-firefox`, click **Load Temporary Add-on…** and choose
  `manifest.json` in this folder. Firefox asks for permission to reach `127.0.0.1`; allow it in the add-on's
  **Permissions** tab. A temporary add-on is removed when Firefox closes.

If you start the agent with `--port`, set the same port in the extension's options.

## Limits

- Protected streams (Netflix, Disney+, Prime Video, Max and others with DRM) may come out **black** in any screen or
  window capture when the browser decodes them in protected hardware. That is the browser's and Windows' choice; the
  extension does not change it. Turning off the browser's hardware acceleration usually makes them capturable, at the
  cost of the browser's own playback.
- The video must be in the tab the browser window shows; a video playing in a background tab is not in the window's
  picture, so the phone shows the cover instead.
- Picture-in-picture windows are separate windows and are not used yet.
