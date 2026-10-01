# Calagopus servers for Notch

A [Notch](https://github.com/Brick-Bread/WNotch) plugin that watches the servers on a Calagopus panel: state and usage on cards, alerts in the pill, and live stats for one server you pick.

## Setup

1. Create an API key in your panel (it needs read access to servers).
2. Install the plugin from Notch's Settings (`Brick-Bread/wnotchcalagopus`) and switch it on. It writes its options to `%AppData%\Notch\plugin-data\brick-bread.calagopus\settings.json`.
3. Set `panelUrl` and `apiKey` there, then switch the plugin off and on. The key is encrypted for your Windows account and removed from the file.

Click a server card to start (or stop) its live connection; click an alerting card to dismiss the alert.

## Options

`pollSeconds`, `maxServerCards`, `includeServers`, `excludeServers`, `selectedServer`, `showSelectedInPill`, `alertOffline`, `alertStateChanges`, `alertThresholds`, `alertPanelUnreachable`, `memoryPercent`, `diskPercent`, `cpuPercent` (0 turns a check off), `thresholdPolls`.

## Build

```
dotnet test
dotnet publish src/NotchCalagopus -c Release -o dist
```

Needs `Notch.Core.dll` from an installed Notch, or `-p:NotchCorePath=<path>`.
