# GV Faction Hangar

Fork of the **Faction Hangar** Space Engineers mod for the GV: Deserts of Kharak server: storage for ships and bases, per player and per faction.

- Original mod: [Faction Hangar](https://steamcommunity.com/sharedfiles/filedetails/?id=3110146798) by Kamikaze ([@kamikaze0310](https://github.com/kamikaze0310)). All credit for the original code goes to him.
- The first commit is the unmodified Workshop source (namespace `CustomHangar`). GVK changes are in later commits.

## Philosophy
- **Stay close to the original.** Changes are fixes and server rules, agreed with the original author, not a rewrite.
- **The rules live in the config.** Where a grid may be loaded, what it costs and how long it takes are config values, not code.

## Requirements
Loaded with the rest of the server mods ([Steam collection](https://steamcommunity.com/sharedfiles/filedetails/?id=2650582206)). The server's settings are in the mod's `!FactionHangarConfig.xml` in the world's `Storage` folder.

## Chat commands
`/fh` is the faction hangar and `/ph` the personal one. The code handles `/fh list`, `store`, `load`, `transfer` and `remove`, and `/ph store`; the in-game help (a bare `/fh` or `/ph`) lists the rest. The rules for players are in the Steam guide.

## Folder map
| Path | What is in it |
| --- | --- |
| `ModScripts/` | The mod's code: session, config, spawn rules, blueprint I/O, timers, network |

## Build
MDK2 mod project. Code lives in `ModScripts/`. Build `FactionHangar.csproj` to deploy to the local Mods folder.

## Quick links
- Hub board and issues: [GV-Server-Mods/GVK-Settings](https://github.com/GV-Server-Mods/GVK-Settings)
- Original mod: [Workshop 3110146798](https://steamcommunity.com/sharedfiles/filedetails/?id=3110146798)
- Steam guide: [players' rules and gameplay](https://steamcommunity.com/sharedfiles/filedetails/?id=2781522559); mod collection: [2650582206](https://steamcommunity.com/sharedfiles/filedetails/?id=2650582206)
- Skills: [se-dev-kharak](https://github.com/GV-Server-Mods/se-dev-kharak)

## Credits
Kamikaze, author of the original Faction Hangar mod.
