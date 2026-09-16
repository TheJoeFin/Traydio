# Changelog

All notable changes to Traydio (formerly Trdo) will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0] - Unreleased

### Changed
- **Renamed to Traydio** — same app, clearer name (#53)
- The tray flyout is now a regular window placed near the click position, fixing DPI and reliability issues with the old flyout (#98, #96, #42)
- LibVLC is now the default playback engine, with Windows Media Foundation kept as a fallback and as an opt-in "Native" mode (broader codec and stream support)
- Volume is now shown as a percentage from 0% to 200%, with 100% (full stream volume) as the default; values above 100% amplify the stream on the LibVLC engine (#105)
- Volume is now remembered per station, so each station keeps its own level and restores it when selected (#16)

### Added
- Groups: collapsible folders in the station list, created from the right-click menu and filled by dragging stations into them the same way stations are reordered. A folder remembers whether it is open. Deleting one keeps its stations, moving them back out to the top level; deleting a folder *and* its stations is a separate command that asks first
- Dividers: a subtle line to break up a long list, with an optional caption. Insert one above any station from its right-click menu, or add one from the list's own menu, then drag it wherever it belongs
- Sort by name, genre, country or date added. This is a view: your own order, folders and dividers are left exactly as they are and come straight back when you switch to Manual, so sorting to find a station never costs you the arrangement you built. While a sort is on, the list says so and dragging is switched off rather than quietly rewriting your order
- Stations added from search now keep the genre, country, language, codec and bitrate that the search already knew, instead of discarding all of it — that is what the new sorting works on
- "Refresh station info" looks up those details for stations that do not have them, for a single station or the whole list. It only ever runs when you ask, only your stations' stream addresses are sent, and it never overwrites a station's name, volume or buffer settings
- Optional song change popup: a brief notification above the taskbar whenever the playing song changes, off by default and enabled in Settings
- Song popup delay: many stations announce a track before it actually starts, so the popup can now be held back by up to a minute until it lines up with the audio. Set an app-wide default in Settings, override it per station in the station's Advanced settings, or right-click the popup itself to adjust the station that's playing. Left-clicking the popup dismisses it. Starting a station skips the delay for that first popup — the track you tune into is already playing, so there is nothing to wait for
- Search filters are now reached by typing rather than scrolling. One box searches every country, language and genre at once — type "ger" for Germany and german, "rock" for rock before classic rock — and what you pick becomes a removable chip under the search box, so what a search is narrowed by stays visible while you read the results. Genres stack up (jazz *and* blues); country and language take one value each, as the directory does. The panel now opens in place of the results instead of as a dropdown inside a 320px window, and the genre list it searches went from the top 200 to the top 1000
- Song popup dwell time: how long the popup stays on screen is now a setting, from 1 to 15 seconds, for anyone who wants longer than the default couple of seconds to read it
- "Allow PC to sleep" setting so the computer can sleep on its normal schedule while radio is playing (#82)
- "Native preferred" playback engine mode preserving the pre-2.0 native-first behavior
- Network awareness: playback is not attempted when offline, and a station that fails because there is no internet connection now shows an in-app error (#102)
- Clearer playback error messages when a stream fails to start — the underlying stream/source error is surfaced, and repeated failures now report "couldn't play after several attempts" instead of retrying silently forever (#103)
- Automatic engine fallback on play: if an engine accepts a stream but never actually produces audio, Traydio now detects the stall within seconds, switches to the other engine and keeps playing, instead of leaving a silent player with no error
- Per-station engine memory: Traydio remembers which engine each station actually plays on (and which one fails) and starts there next time, with a "Reset remembered engines" button in Settings
- Stream diagnosis on failure: when a station won't play, Traydio probes the stream and explains why — server offline, address not found, HTTP error, or the address being a playlist file rather than a stream (in which case it names the URL to use instead)
- LibVLC's own log is now captured into the app log, so a LibVLC failure records the actual reason rather than a generic "LibVLC playback error"; Media Foundation failures now include a decoded HRESULT
- Log entries keep the stream's port and path (query strings are still redacted), so stations sharing a host can be told apart when diagnosing a problem
- Export favorite songs from the Favorites page as a CSV file (the format playlist transfer services like Soundiiz and TuneMyMusic take to build a Spotify or Apple Music playlist) or as an XSPF playlist for players such as VLC. Radio metadata has no track URL, so the formats that carry artist and title alone are the ones that actually import somewhere
- Exports can archive what they send: archived songs stay in the favorites list, marked "Exported", and are skipped by later exports, so running an export every so often only ever hands over what is new. Archiving is a checkbox on the export, an export can be switched back to the full list at any time, and a song — or the whole history — can be un-archived to send it again
- Cast: right-click the Play button and choose "Cast…" to send the audio to a Chromecast or other renderer on your network. The device search only runs while the picker is open, so nothing listens on the network the rest of the time. Casting pins playback to the LibVLC engine (the native engine can't cast), and "Stop casting" brings the audio back to this PC
- Sonos: the cast picker now also finds Sonos players and groups on your network. A Sonos fetches the station's stream itself, so nothing is re-encoded on this PC; play, pause, stop and the scrub bar for local music are relayed to the speaker, and what the speaker reports (including a paused or resumed state from the Sonos app, and the stream's song titles) shows in Traydio. Local music folders are served to the speaker from this PC for the duration. While casting to a Sonos, a second volume slider with a speaker icon appears under the stream volume to control the speaker's own level and mute

### Fixed
- The app no longer freezes when a stream fails under LibVLC and the native engine takes over (#109). LibVLC reports a failure from one of its own threads, and switching engines in response tore that same player down from inside the callback — something LibVLC forbids, and it deadlocked the whole process, leaving "Buffering…" on screen and End Task as the only way out. Its events are now handed on from a worker thread instead. The switch that follows also starts the new engine on a station's very first play, where it used to prepare it and stop: nothing played, nothing said why, and the popup that should have explained it never appeared
- Cancelling a drag in the station list no longer restarts the stream. Dropping a station outside the list, or pressing Escape mid-drag, was treated as a completed reorder and re-initialised playback of whatever was playing
- The "No Stations Yet" panel now disappears as soon as the first station is added, instead of lingering until the page was reopened
- Playback errors no longer appear for stations that are playing perfectly well. A failure reported while the fallback engine was still starting (or while stream diagnosis was probing the network) was raised against the hidden tray popup, where it waited and greeted the user the next time they opened the flyout. Errors are now held by a service that re-checks them against live playback — including the audio loopback, so an engine claiming to play while producing silence is still reported — and are dropped when playback recovers, when the station changes, or when they simply go unseen for too long. An error already on screen is taken down if the station starts playing behind it
- "Copy diagnostics" now copies the current log. It was unable to open the active log file at all (a file-sharing mismatch with the log writer) and silently fell back to the previous rolled-over generation, and it did not wait for buffered lines to reach disk — so the most recent entries, the ones worth reporting, were the ones most likely to be missing

## [1.1.0] - 2025

### Added
- System tray integration for background playback
- Multiple radio station support
- Stream watchdog for automatic reconnection
- Start with Windows option
- Volume control with persistent settings
- Modern Fluent Design UI
- About page with app information
- Settings page for customization

### Features
- Add/Edit/Remove radio stations
- Station list with selection indicators
- Automatic stream recovery on connection loss
- Theme-aware tray icons
- Persistent volume and station preferences

## [1.0.0] - 2025 (Beta)

### Added
- Initial release
- Basic radio streaming functionality
- WinUI 3 interface
- .NET 9 support

---

## Version History Legend

- **Added** for new features
- **Changed** for changes in existing functionality
- **Deprecated** for soon-to-be removed features
- **Removed** for now removed features
- **Fixed** for any bug fixes
- **Security** for vulnerability fixes
