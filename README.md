# MegabonkAI

A MelonLoader mod that plays [Megabonk](https://store.steampowered.com/app/3405340/Megabonk/) on its own — it explores the map, loots chests, charges shrines, dodges area attacks, picks its upgrades, and fights bosses at range. Built for hands-off streaming.

It hooks the game's own state rather than reading the screen, so it knows exact enemy positions, item rarities, gold, and terrain, and reacts at frame rate.

> **Single-player only.** This is a bot playing the game for you. Don't use it to compete on leaderboards, and don't use it in any context where automation would be unfair to other players.

---

## Features

**Navigation**
- **A\* pathfinding** over a walkability grid sampled from world geometry (Megabonk's maps are procedural — there's no NavMesh). Grid cells are probed with raycasts, rejected on slopes steeper than the player can climb or where there's no headroom, and cached.
- Connectivity respects real movement limits: climb height, survivable drop, and a midpoint check that distinguishes a ramp from a wall.
- The search runs **incrementally** across frames, so long routes complete without frame hitches.
- Routes prefer the middle of walkable ground over scraping edges, and the bot **learns**: ground it fails to traverse is temporarily blocked so replans go around.

**Play**
- Prioritised targets: boss drops → chests → charge shrines → item sources → pots, weighted by value against travel distance, with commitment so it doesn't flip-flop between targets.
- Only targets chests it can actually afford, and never triggers "insufficient funds".
- Holds position in charge shrines until they complete, and handles the chest / level-up / Moai / greed-altar windows.
- Upgrade choice scores actual `StatModifier` values per stat — survivability and damage weighted up, `Difficulty` / `EnemySpeed` / `EliteSpawn` weighted **negative** — with rarity taking precedence.
- Avoids the shady guy, microwave, cursed shrines, eggs and the suspicious bush.

**Survival**
- Detects telegraphed area attacks (`CircleWarning`, `TubeWarning`, tornadoes) and treats them as hard no-go zones.
- Escape directions are chosen by **open space first**, so it doesn't reverse into a wall — with a dedicated *breakout* behaviour for when a horde has it cornered.
- Boss fights are a duel at weapon range: it reads the equipped weapons' `spawnProjectileRange`, holds a kite ring at ~72% of it, circles constantly, and widens the ring when hurt.

**Speedrun mode** (optional)
Uses Megabonk's actual movement tech, which is *not* Quake-style air-strafing — per the community movement guides, speed comes from the angle between camera facing and travel direction (deadzone under ~30°, sweet spot ~45°, loss past ~60°), the camera must be *turning*, diagonal input beats straight, and bunny hopping only maintains speed rather than adding it. The mode sweeps the view continuously through the productive band, holds a diagonal, hops to dodge ground friction, and slides down descents. It stands down near enemies, on climbs, and on final approach, where the tech costs more than it gains.

**Overlay**
- In-world path visualisation: the computed route drawn as a smoothed, colour-coded line (green = complete route, yellow = partial, blue = searching, red = no route) with a goal beacon.
- A HUD panel showing current behaviour, target, route progress, AoE and loot counts.
- An optional stabilised chase camera.

---

## Install

1. Install [MelonLoader](https://melonwiki.xyz/) into Megabonk and **run the game once** so it generates `MelonLoader/Il2CppAssemblies`.
2. Download `MegabonkAI.dll` from [Releases](../../releases).
3. Drop it into `Megabonk/Mods/`.
4. Launch the game and start a run.

Tested against **Megabonk 1.0.69**, Unity 2023.2.22f1, MelonLoader 0.7.3. Other versions may work but aren't verified — the mod reads game classes by name, so a big patch can break it.

## Controls

| Key | Action |
|---|---|
| `Num1` / `F9` | Toggle AI control |
| `Num2` / `F8` | Toggle speedrun mode |
| `Num3` / `F10` | Toggle path visuals + HUD |
| `Num4` / `F7` | Toggle chase camera |

The AI starts **disabled** — nothing touches your input until you press the key.

## Build

Requires the [.NET SDK](https://dotnet.microsoft.com/download) (6.0 or newer).

```bash
dotnet build mod/MegabonkAI.csproj -c Release
```

If Megabonk isn't at the default Steam path:

```bash
dotnet build mod/MegabonkAI.csproj -c Release -p:MegabonkDir="D:\Games\Megabonk"
```

Output lands in `mod/bin/Release/net6.0/MegabonkAI.dll`. Copy it to `Megabonk/Mods/`.

## How it works

| File | Role |
|---|---|
| `mod/Core.cs` | Behaviour: target selection, path following, combat, evasion, UI handling, HUD and visuals. Drives movement by patching `PlayerInput.MovementInput` with Harmony. |
| `mod/Pathfinder.cs` | Incremental A\* and the lazily-sampled walkability grid. |

The mod writes a running commentary to MelonLoader's console and `MelonLoader/Latest.log` — current mode, target, route state, scored upgrade offers, and why targets were rejected. That log is the fastest way to diagnose odd behaviour.

## Known limitations

- Loot on isolated peaks with no walkable route is detected and skipped rather than reached.
- Speedrun mode trades precision for pace; it stands down in tight terrain.
- Terrain sampling is raycast-based, so unusual geometry can still produce the occasional bad route.

## Contributing

Issues and PRs welcome. When reporting movement or targeting problems, please include the relevant section of `MelonLoader/Latest.log` and a screenshot — the log lines carry the bot's reasoning and make root-causing far quicker.

## Credits

- Movement mechanics informed by the Megabonk community's bunny-hop and drift-boost guides.
- Built with [MelonLoader](https://melonwiki.xyz/), [HarmonyX](https://github.com/BepInEx/HarmonyX) and [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop).
- Megabonk is by **Ved**. This project is unaffiliated fan tooling and ships no game assets or code.

## License

[MIT](LICENSE)
