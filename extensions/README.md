# Local browser handoff

Firefox is first; Chromium/Chrome/Edge/Brave use the separate MV3 extension. These private development extensions have not been published, signed or installed into the owner's browser.

After a verified desktop installer registers the `keaclip` protocol with your explicit choice, Firefox can load `firefox/manifest.json` as a temporary extension through `about:debugging`. Chromium's developer-mode Load unpacked can select `chromium`. Browser/OS policy may ask before opening the external app. Temporary Firefox extensions disappear after restart; final signed distribution is a separate gate.

Right-click a page/link/video/audio or click the toolbar button. The extension sends only a validated HTTP(S) URL to the desktop. It needs no broad site permissions, storage or downloader logic. The desktop validates the URL again and never downloads automatically. Protocol registration is an installer option, not a background mutation.

Native handoff must be tested in real Firefox and Chromium before claiming integration acceptance. Unit/fake event checks and protocol parsing do not establish browser UX.
