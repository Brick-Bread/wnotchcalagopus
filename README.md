# Calagopus servers for Notch

A [Notch](https://github.com/Brick-Bread/WNotch) plugin for a Calagopus panel: an overview card, alerts in the pill, and a tab of its own to browse your servers, watch one live, run console commands and control its power.

Needs Notch 0.7.0 or newer.

## Setup

1. Create an API key in your panel (it needs access to your servers).
2. In Notch, open Settings, then Plugins, type `Brick-Bread/wnotchcalagopus` into the install box and press **Install**. Tick the plugin and press **Save**.
3. Open the plugin's **Options** in Settings, enter the panel address and the API key, and press **Save**. The key is stored encrypted for your Windows account and never shown again.

Later updates show an **Update and restart** button under the plugin in Settings.

## Using it

- The **Plugins** tab holds one overview card: how many servers are online, and any alert. Click it to dismiss alerts.
- The plugin's own tab lists your servers. Click one to see its state, CPU, memory, disk, network and uptime, with the live console below and a box to send commands.
- **Start**, **Restart**, **Stop** and **Kill** are at the top right of a server's page. Only the ones that make sense for the server's state are enabled, and Kill asks you to click twice.
- The back button returns to the list of servers.

## Options

All options are in Settings under the plugin: panel address, API key, how often to check, which servers to show or hide, which alerts to raise, and the memory, disk and CPU limits (0 turns a check off).

## Build

```
dotnet test
dotnet publish src/NotchCalagopus -c Release -o dist
```

Needs `Notch.Core.dll` from an installed Notch, or `-p:NotchCorePath=<path>`.
