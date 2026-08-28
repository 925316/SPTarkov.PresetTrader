# PresetTrader

A server-side mod for SPT 4.1.x: a trader that sells your own weapon builds plus ready-made headgear, armor, rig and plate presets — unlimited stock, automatic prices.

## Requirements

- [SPT 4.1.x](https://sp-tarkov.com)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (build only)

## Install

- Download the zip from the [latest release](https://github.com/925316/SPTarkov.PresetTrader/releases/latest) and extract it into your server root (it contains the `SPT_Runtime` tree), or
- Copy only the `PresetTrader` folder into `<server root>\SPT_Runtime\user\mods\`

Start the server. Weapon builds saved in-game show up at the trader within ~30 seconds.

## Presets

Headgear, armor, rig and plate presets live in `user\mods\PresetTrader\db\` as plain JSON — edit and restart the server, no rebuild needed:

```json
[
  {
    "RootTpl": "5b40e2bc5acfc40016388216",
    "Attachments": [
      { "Slot": "Helmet_top", "Tpl": "657112234269e9a568089eac" }
    ]
  }
]
```

- `RootTpl`: the `_tpl` of the sold item; an empty `Attachments` list sells it as a plain item
- `Attachments`: children pre-installed on the root, `Slot` must match one of its slot names
- Keys are case-sensitive (`RootTpl`, `Attachments`, `Slot`, `Tpl`)

Prices are automatic: per item the higher of handbook and ragfair baseline price, summed over the preset. Unlimited stock on everything.

## Build

Double-click `build.bat`, or run:

```powershell
dotnet build SPTarkov.PresetTrader.sln -c Release
```

Output goes to `Build\Release\SPT_Runtime\user\mods\PresetTrader`.

## License

[AGPL-3.0](LICENSE)

## Post

[sp-mod.com](https://sp-mod.com/mod/2966/presettrader)
