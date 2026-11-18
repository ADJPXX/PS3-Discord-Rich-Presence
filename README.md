# PS3 Discord Rich Presence

Display the activity of a physical PlayStation 3 on Discord. The Windows application reads the console status from webMAN MOD over the local network and publishes the activity through Discord Desktop.

## Features

- Detects the XMB, multiMAN and the running game when webMAN provides that information.
- Publishes the current game title to Discord Rich Presence.
- Looks up game cover art by title ID and uses the registered `xmb` image as a fallback with the built-in Discord application.
- Reads CPU and RSX temperatures for the status window and activity when enabled.
- Reconnects to Discord and the PS3, with longer delays during extended outages.
- Shows the current activity, console address, connection states and time of the last check in a resizable desktop dashboard.

## Requirements

- Windows 10 or later on an x64 PC.
- Discord Desktop running in the same Windows session.
- A physical PS3 with webMAN MOD enabled.
- PC and PS3 on the same local network.

HEN and CFW configurations are supported when the webMAN web server is active. RPCS3 is not supported.

## Getting started

1. Find your PS3 IP address under **Settings → Network Settings → Connection Status List** on the console.
2. Download `PS3DiscordRichPresence.exe` from the [latest release](https://github.com/ADJPXX/PS3-Discord-Rich-Presence/releases).
3. Put the executable in a folder where your Windows user can create files, then run it.
4. On the first launch, the application creates `PS3config.json` beside the executable and waits for a valid PS3 IP. Edit the `Ip` value and save the file as UTF-8.

For example:

```json
{
  "Ip": "192.168.1.35",
  "hidetotray": false
}
```

The application accepts a bare IP address. Do not add `http://`, a port or a webMAN path. The default Discord application ID and the other settings have built-in values, so `Ip` is the only required setting.

5. Keep Discord Desktop open. With the example configuration, the dashboard opens automatically. On the PS3 home menu, **Overview** shows **XMB**. When Discord accepts the update, its connection card reads **Activity confirmed**.
6. Start a game. The next successful poll updates the title and, when available, its cover image.

## Configuration

The configuration file is `PS3config.json` beside the executable. Choose **Edit configuration** in the dashboard or the tray icon's right-click menu to open it in Notepad. After saving, choose **Reconnect** in the dashboard or **Reload configuration** in the tray menu to apply your changes. Restarting also reads the file.

| Property | Default | Description |
| --- | --- | --- |
| `Ip` | Required | PS3 IP address, without a scheme, port or path. |
| `ClientId` | `1528636206638694400` | Discord application ID as a positive JSON integer. Omit this property to use the built-in application. |
| `UpdateIntervalSeconds` | `3` | Polling interval in seconds. Values below 3 are raised to 3. |
| `ReconnectIntervalSeconds` | `10` | Retry delay in seconds. Values below 10 are raised to 10. Extended PS3 outages use progressively longer delays. |
| `ShowTemperature` | `false` | Include available CPU and RSX readings in the status window and Discord activity. |
| `hidetotray` | `true` | Set to `true` to run in the notification area or `false` to show the dashboard. Older files can use `MinimizeToTray`. If both keys exist, `hidetotray` takes precedence. |
| `StartWithWindows` | `false` | Start the application at sign-in for the current Windows user. |

The application also accepts larger polling intervals, within the limit supported by .NET timers. Configuration files have a 64 KiB size limit.

## Dashboard and notification area

- **Overview** shows the latest console activity and the status of each connection. With `ShowTemperature` enabled, available CPU and RSX readings appear under the activity title.
- **Diagnostics** shows the monitor's latest report, including webMAN and Discord errors. **Copy report** copies that text to the clipboard.
- In tray mode, the monitor runs in the background, including during setup and connection errors. To open the dashboard, set `hidetotray` to `false`, save and select **Reload configuration**.
- **Exit** stops the monitor, waits for pending work to finish, releases its resources and closes the application.

The tray icon tooltip shows the current top-line status. The status window also distinguishes an unreachable console, an HTTP response without a recognized webMAN status and a confirmed Discord activity.

The app preserves the existing Discord activity through the first four consecutive unsuccessful PS3 reads. On the fifth failed read, it asks Discord to remove the activity. If the console responds again, it publishes the current state.

## Troubleshooting

### The PS3 shows “No response”

From the same PC, open `http://<PS3-IP>/cpursx.ps3` in a browser, replacing `<PS3-IP>` with the address in `PS3config.json`. The app requests that webMAN endpoint directly and allows 12 seconds for the response.

- If the browser also times out, check the IP address, local network and webMAN web server.
- If the browser shows the webMAN page but the app times out, check per-application VPN routing. A WireSock rule containing `Discord` can also match `PS3DiscordRichPresence.exe`. Add only the PS3 IPv4 address to that VPN profile's excluded IPs, using `/32` for one address, then reapply the profile. See [WireSock's excluded IP settings](https://wiresock.net/documentation/wiresock-secure-connect/advanced-parameters.html#disallowedips-optional).

### The PS3 responds, but webMAN status is unavailable

Read the diagnostic below the PS3 status. It shows the HTTP error, body timeout or unrecognized page detail. Confirm that `/cpursx.ps3` displays the webMAN status page.

### Discord shows offline

Open Discord Desktop in the same Windows session as the monitor. Use the IPC message in **Diagnostics** to distinguish a missing Discord pipe, access denied and a rejected handshake. If you configured a custom `ClientId`, verify it or remove that property to use the default application.

### Discord confirmed the activity, but others cannot see it

Check Discord's Activity Sharing settings and who is allowed to view the activity. The local acknowledgement confirms that Discord accepted the update from this application. It does not control your account's sharing preferences. See [Discord Activity Sharing](https://support.discord.com/hc/en-us/articles/7931156448919-Activity-Sharing-on-Discord-FAQ).

### A game appears as “Unknown”

The app detected a game, but webMAN did not provide a recognized game title in the status page. Include the title ID and relevant webMAN status details when reporting the issue.

### Cover art is missing

The app looks up cover art on GameTDB by the title ID. Temporary lookup failures and missing regional covers use the registered fallback image when one is configured.

## Build and tests

The project targets .NET 10 and WPF. On Windows with the .NET 10 SDK installed, run these commands from the repository root:

```powershell
dotnet restore .\PS3DiscordRichPresence.sln
dotnet test .\PS3DiscordRichPresence.sln -c Release --no-restore
```

The xUnit suite uses NSubstitute and local test doubles for configuration files, HTTP, Discord IPC, Windows named pipes and the WPF dispatcher. The release workflow runs the suite before publishing a self-contained, single-file `win-x64` executable. End users do not need to install the .NET runtime separately.

## Contributing

Bug reports and pull requests are welcome. For connection reports, include the messages from **Diagnostics**, the browser result for `/cpursx.ps3` and the PS3 title ID if available.

## License

This project is licensed under the [MIT License](LICENSE).
