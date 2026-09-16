# Navrey

Navrey is a fork of the [ClassicUO](https://github.com/ClassicUO/ClassicUO) Ultima Online client
that can be driven from outside the game window. A shell prompt, a script, or an AI agent such as
Claude Code can walk your character, fight, loot, bank, shop and talk, using the same connection
the game window uses. You can watch it happen in the window, or run with no window at all.

That is the change to ClassicUO itself. The repo adds three more things on top of it:

- **Agent skills.** Step-by-step guides in `.claude/skills/` that teach an agent how to log in,
  navigate, fight, loot, bank, shop, use moongates and runebooks, recover from death, talk to
  other players, and more. Claude Code reads them on its own; any agent that can read a folder
  of Markdown can use them.
- **A point-of-interest lookup tool.** `closest bank`, `findpoi moongate` and `goto <place>`
  work from landmark data for your shard, and converters under `.claude/skills/uo-poi-data/`
  build that data from the marker packs most shards publish. See "Map data" below.
- **Premade scripts.** Background scripts that carry a task on their own and hand control back
  with a reason: a melee combat loop with pursuit, healing, looting and retreat, and a guarded
  walker that stops when a hostile comes into range. Melee is the only fighting style covered so
  far; archery, magery, taming and other builds are open for contribution. See "Premade
  scripts" below.

## What you can do with it

Here it is in action:

[![Watch a fully autonomous demo on YouTube](https://img.youtube.com/vi/AS-_oidzefY/maxresdefault.jpg)](https://youtu.be/AS-_oidzefY)

Ask in plain language and the agent does the rest:

- "Walk to the bank and deposit everything I looted."
- "Hunt zombies and skeletons near Old Haven, heal yourself, and hand back when the ground is empty."
- "Buy 50 bandages from the healer, then go back to where we were."
- "Take the moongate to Britain."
- "Who is standing next to me and what are they wearing?"
- "Mark a rune here and add it to the runebook."

Each of those is backed by a guide in `.claude/skills/` (logging in, navigation, combat, looting,
containers, banking, buying and selling, moongates, mounts, runebooks, death recovery, talking to
other players, inspecting players, and building map data for your shard). The agent reads the
guide it needs and drives the client. You do not have to learn the commands yourself, though you
can if you want to.

## Setting up

You need:

- **The .NET 10 SDK.** The launcher looks in `~/.dotnet` first, then on your `PATH`.
- **Ultima Online client files** matching your shard's client version, from your own legally
  obtained install. This repo ships no game assets. Any folder works as long as `tiledata.mul`
  is in it.
- **`python3`** (3.10 or newer) and **`jq`**. The agent's scripts use them.
- **An account on the shard** you want to play on.
- **Claude Code** (or another agent that can read the `.claude/skills/` guides), if you want the
  agent to drive. Everything also works from the prompt by hand.

Configuration is exactly two files at the repo root. Nothing on the command line overrides them,
and the client never writes them back, so what you put in them is what runs.

### 1. `settings.json`: the shard and the client

This is ClassicUO's normal settings file. Edit these fields and leave the rest alone:

| field                                  | what to put                                                                                |
| -------------------------------------- | ------------------------------------------------------------------------------------------ |
| `ip`, `port`                           | Your shard's login server, from its website.                                               |
| `client_version`                       | The client version the shard expects, for example `7.0.114.2`. The wrong one breaks login. |
| `uo_directory`                         | Absolute path to your UO client files.                                                     |
| `poi_directory`                        | Optional. Where the map landmark data for this shard lives. See "Map data" below.          |
| `last_server_num`, `last_server_name`  | Which shard to pick on the shard list. Single-shard logins are entry 1.                    |
| `username`, `password`, `save_account` | Leave empty and `false`. The account goes in `.env` so it never lands in a tracked file.   |
| `auto_login`, `reconnect`              | Leave `false`. The account in `.env` already logs you straight in.                         |

Window size, FPS, music and the like can be set here too.

### 2. `.env`: your account

```bash
cp .env.example .env
```

Fill in `UO_USER` and `UO_PASS`. That is all this file holds. It is gitignored. If it is
missing, the client starts at the login screen and you log in by hand.

### 3. Build and run

```bash
~/.dotnet/dotnet build src/ClassicUO.Client/ClassicUO.Client.csproj
~/.dotnet/dotnet build cli/Navrey.Cli.csproj
cli/navrey
```

`cli/navrey` starts the client, logs in, and gives you a `navrey>` prompt. Variants:

```bash
cli/navrey --headless        # no game window
cli/navrey --attach          # reconnect the prompt to a client that is already running
cli/navrey --no-autologin    # stop at the login screen
```

The client keeps running when you leave the prompt. Running `cli/navrey` again attaches to it
rather than starting a second client. `show` and `hide` at the prompt bring the game window up or
put it away without disconnecting.

To confirm you are in the world:

```bash
jq -c '{inGame, charName, charPosX, charPosY}' /tmp/cuostate.json
```

If `inGame` never turns true, look in `/tmp/cuostdout.log` and `/tmp/cuolog`. A wrong client
version, a wrong data folder, or a bad `.env` each leave a plain message there.

### 4. Switching shards

Change the shard fields in `settings.json`, put that shard's account in `.env`, and restart.
Keeping one `settings.json` per shard outside the repo and copying it in is the easy way to flip
between them.

## Playing with the agent

Open Claude Code in the repo. `CLAUDE.md` tells it how the client works.
Then just start prompting. A few things worth knowing:

**Combat scripts are written for a build.** The shipped one covers a character that fights with a
melee weapon and heals with bandages. It keeps the character adjacent to its target, watches its
health, runs when it needs to, loots its own kills, and hands control back when there is nothing
left to fight or something it should not fight shows up. An archer, a mage, a tamer, or a
character that heals with potions or spells needs its own script. The agent can write them; ask it to start from the
`uo-combat-loop` guide.

**Handing control back to model from scripts** A combat script stops on its own when the ground is empty, when
it cannot reach the spawn, when it cannot shake something dangerous, or when it is not healing.
Its last line says why and where the character is. The agent then decides what to do next. That is
the normal rhythm of a hunt: fight, the script exits with reason when blocked from combat, model decides what to do.

## Premade scripts

Two scripts ship with the repo. Both run in the background, print what they are doing, and stop
on their own with a last line that says why. The agent launches them when a task calls for them;
you can also run them yourself. Paths are relative to the repo root.

Combat is melee only for now. A script for another build (an archer, a mage, a tamer, or anyone
who heals with potions or spells) would be a welcome contribution; start from the `uo-combat-loop`
guide and the melee script, which already carry the pursuit, looting and hand-back logic.

| Script                                                         | How to use                                                                                                                                                                                                                                                                                                                                    | When to use                                                                                                                                                                                                                                                                                                                                             |
| -------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `combat_movement_melee.py` in `.claude/skills/uo-combat-loop/` | `python3 .claude/skills/uo-combat-loop/combat_movement_melee.py --include species:Zombie --include species:Skeleton` then attack something, or let it pick a target itself. `--include` restricts it to the named species, `--avoid` does the reverse. Run `autoheal.py` from the same folder alongside it, since this script never bandages. | Any melee fight. It pursues the target and stays adjacent, loots the kill, runs from low health, an avoided creature or a swarm, and hands control back the moment nothing is left to fight, saying whether the ground is bare, the spawn is unreachable, or something it refuses to fight is there.                                                    |
| `safe_goto.py` in `.claude/skills/uo-navigation/`              | `python3 .claude/skills/uo-navigation/safe_goto.py 3496 2533` or `... bank`. Add `--watch` to stay on guard after arriving. Every exit ends on a `RESUME:` line with the outcome, position, health, and distance short of the destination.                                                                                                    | Walks across ground that has hurt this character before, or when told to walk carefully. It stops the instant a hostile comes within 15 tiles and retreats if that creature is fighting you. With `--watch` it is the standing guard while nothing else is running. A plain `goto` is right for towns, roads, and any walk made while already fighting. |

## Playing by hand

`cli/navrey` opens the same `navrey>` prompt. Game events stream in while you type: speech, combat,
containers, shops. Up and down arrows recall history. `help` prints this list with the short
aliases. Serials are the `0x...` ids that `mobiles`, `items` and `inv` print next to each thing.

**Moving**

| command                                   | what it does                                                                                                                         |
| ----------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| `goto <x> <y> [z]` or `goto <place>`      | Walk somewhere. Opens doors on the way and settles for the nearest reachable tile. Add `avoid:<x>,<y>[,<r>]` to route around a spot. |
| `gotoexact <x> <y> [z]`                   | The same, but only that exact tile counts as arriving.                                                                               |
| `travel <x> <y>` or `travel <place>`      | A long trip, done in hops.                                                                                                           |
| `walk <n\|s\|e\|w\|ne\|nw\|se\|sw> [run]` | One step.                                                                                                                            |
| `stop`                                    | Cancel the current walk or long-running command.                                                                                     |
| `opendoor`                                | Open the door in front of you.                                                                                                       |
| `path <x> <y>`                            | Plan a route and print it without walking.                                                                                           |
| `canwalk [x y]`                           | Whether a tile can be walked on, and if not, why.                                                                                    |
| `pos`                                     | Where you are, which way you face, which map.                                                                                        |

**Looking around**

| command                       | what it does                                                  |
| ----------------------------- | ------------------------------------------------------------- |
| `mobiles [range]`             | Everyone and everything alive nearby, nearest first.          |
| `nearest`                     | The closest mobile.                                           |
| `items [range]`               | Items on the ground nearby.                                   |
| `allitems`                    | Every tracked item, including what is inside open containers. |
| `tiles <x> <y>`               | Everything on a tile, with its flags.                         |
| `los [range]`                 | Whether you have line of sight to each nearby mobile.         |
| `debuglos <x> <y>`            | The sight line to a point, tile by tile.                      |
| `click <serial>`              | Single-click something to ask its name.                       |
| `props <serial\|self\|layer>` | An item's tooltip: weight, durability, damage, resists.       |
| `gear <serial\|self>`         | What a character is wearing, with hues, and their title.      |
| `hp <serial>`                 | Ask the server for a mobile's status bar.                     |
| `read <serial>`               | Read a book into the log, page by page.                       |
| `world`                       | Login state, map, and how much the client is tracking.        |

**Yourself**

| command           | what it does                                                                                       |
| ----------------- | -------------------------------------------------------------------------------------------------- |
| `status`          | HP, mana, stamina, stats and gold.                                                                 |
| `skills`          | Your skill list.                                                                                   |
| `useskill <name>` | Use a skill, such as `useskill arms lore`. If it asks for a target, answer with `target <serial>`. |
| `dead`            | Whether you are a ghost.                                                                           |
| `inv`             | What you are wearing and what is in your backpack.                                                 |
| `backpack`        | Open the backpack. Its contents do not show until it has been opened once.                         |
| `state`           | Where the state file is and what it holds right now.                                               |

**Items and containers**

| command                            | what it does                                               |
| ---------------------------------- | ---------------------------------------------------------- |
| `use <serial>`                     | Double-click: open, use, or equip.                         |
| `container <serial>`               | List what is inside a container.                           |
| `get <serial> [amount]`            | Pick something up into your backpack.                      |
| `drop <item> <container> [amount]` | Move an item into a container.                             |
| `dropground <item> [amount]`       | Drop an item at your feet.                                 |
| `equip <serial>`                   | Wear something from your backpack.                         |
| `unequip <serial>`                 | Take something off into your backpack.                     |
| `target <serial\|self>`            | Answer a target cursor, for example after using a bandage. |
| `canceltarget`                     | Dismiss a target cursor.                                   |

**Fighting**

| command           | what it does                                                                                                        |
| ----------------- | ------------------------------------------------------------------------------------------------------------------- |
| `attack <serial>` | Attack a mobile.                                                                                                    |
| `attacknearest`   | Attack the nearest hostile you can see. Never a blue innocent.                                                      |
| `war`             | Toggle war mode.                                                                                                    |
| `cast <spell>`    | Cast a spell by name, such as `cast heal`. If it asks for a target, answer with `target <serial>` or `target self`. |

**Riding**

| command          | what it does                                                  |
| ---------------- | ------------------------------------------------------------- |
| `mount [serial]` | Ride a mount. Leaves war mode first, so you do not attack it. |
| `dismount`       | Get off.                                                      |

**Talking**

| command                                             | what it does                                          |
| --------------------------------------------------- | ----------------------------------------------------- |
| `say <text>`                                        | Speak. `say bank` opens your bank box next to a bank. |
| `whisper <text>` / `yell <text>` / `emote <text>`   | The other speech kinds.                               |
| `party <text>` / `guild <text>` / `alliance <text>` | Party, guild and alliance chat.                       |

**Shops and trades**

| command                                         | what it does                                                                   |
| ----------------------------------------------- | ------------------------------------------------------------------------------ |
| `shop`                                          | What the open vendor sells. Open it with `say <vendor name> buy`.              |
| `buy <serial> <amount> [...]`                   | Buy from the open vendor.                                                      |
| `selllist`                                      | What the open vendor will buy from you. Open it with `say <vendor name> sell`. |
| `sell <serial> <amount> [...]`                  | Sell to the open vendor.                                                       |
| `trades`                                        | Open player-to-player trade windows.                                           |
| `accepttrade [serial]` / `canceltrade [serial]` | Accept or cancel a trade.                                                      |

**Menus and dialogs**

| command                                                      | what it does                                                                   |
| ------------------------------------------------------------ | ------------------------------------------------------------------------------ |
| `gumps`                                                      | List the dialogs the server has open.                                          |
| `gumpresponse <button> [switch ...] [text:<id>=<value> ...]` | Press a button on the open dialog, with any options ticked and text filled in. |
| `contextmenu <serial>`                                       | Right-click something and list its menu entries.                               |
| `contextpick <serial> <index>`                               | Choose one of those entries.                                                   |
| `prompt <text>` / `promptcancel`                             | Answer or decline a text prompt, such as naming a rune or a pet.               |
| `closepaperdolls [serial]`                                   | Close open paperdolls.                                                         |

**Places**

| command              | what it does                                                  |
| -------------------- | ------------------------------------------------------------- |
| `closest <category>` | The nearest bank, healer, moongate and so on. Needs map data. |
| `findpoi <text>`     | Search places by name, on every facet.                        |
| `poi`                | What map data is loaded, and its categories.                  |

**The client**

| command             | what it does                      |
| ------------------- | --------------------------------- |
| `show` / `hide`     | Bring up or hide the game window. |
| `quiet` / `verbose` | Less or more output in the log.   |
| `help`              | This list, with aliases.          |

Two files are handy while playing. `/tmp/cuolog` is everything the client sees, so `tail -f` it in
another terminal. `/tmp/cuostate.json` is your character right now: position, health, stats,
equipment, backpack, what you are fighting, and whether a walk is still in progress. It is safe to
read as often as you like:

```bash
jq -r '"\(.charName) \(.hits)/\(.maxHits) hp at (\(.charPosX), \(.charPosY))"' /tmp/cuostate.json
jq -c .nav /tmp/cuostate.json          # {"active":false,"status":"arrived",...}
```

## Map data

`closest`, `findpoi` and `goto <place>` need landmark data for your shard's map: where the banks,
healers, moongates and dungeon entrances are. None ships here, because every shard's map is
different. Without it, everything else works; only lookups by name or category do not.

### What the client loads

A directory of `.json` files, named by `poi_directory` in `settings.json`. Every `.json` in it is
loaded and merged. Each file is one object whose `cells` map holds arrays of points:

```json
{
  "gridSize": 100,
  "cells": {
    "13,16": [
      {
        "x": 1336,
        "y": 1997,
        "z": 0,
        "map": 1,
        "name": "Britain Moongate",
        "category": "moongate",
        "major": true
      },
      {
        "x": 1332,
        "y": 1690,
        "z": 0,
        "map": 1,
        "name": "Britain Bank",
        "category": "bank"
      }
    ]
  }
}
```

The cell keys are bookkeeping from the converters and the client ignores them, so a hand-written
file with one cell holding every point is valid. Per point:

| field      | required | meaning                                                                                                                                                   |
| ---------- | -------- | --------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `x`, `y`   | yes      | world tile                                                                                                                                                |
| `z`        | no       | floor, for stacked buildings. Defaults to 0.                                                                                                              |
| `name`     | no       | what `findpoi` searches and `goto <name>` accepts                                                                                                         |
| `category` | no       | what `closest <category>` groups by. One lower-case word.                                                                                                 |
| `map`      | no       | facet: 0 Felucca, 1 Trammel, 2 Ilshenar, 3 Malas, 4 Tokuno, 5 Ter Mur. Leave it out only on a single-facet shard; a point without it matches every facet. |
| `major`    | no       | `true` for notable entries. Cosmetic.                                                                                                                     |

The agent's guides look up these categories by name, so use them exactly: `bank`, `healer`,
`moongate`, `stables`, `down` (a dungeon entrance, at the tile you walk into) and `teleporter`.
Any other category is yours to choose, but keep it consistent: `blacksmith` and `blacksmiths` are
two different categories to `closest`.

### Converting a marker pack

Most shards publish map markers in the UOAM `.map` format that the classic world-map tools and
ClassicUO's own world map read. One file per theme, one landmark per line:

```
+INN: 5165 30 1 Seeker's Inn
^type   x    y  facet name
```

A leading `+` marks a notable entry, `-` the rest. Convert the whole pack:

```bash
python3 .claude/skills/uo-poi-data/map_to_poi.py --src <dir of .map files> --out <poi dir>
```

It writes one `.json` per `.map`, turns the marker type into the category (lower-cased, with the
few renames the guides need such as `STAIRSDOWN` to `down` and `STABLE` to `stables`), keeps the
facet from each line, and prints the counts plus any line it could not parse.

### Converting a CSV you wrote

For a shard with no marker pack, or to add the handful of places you actually use, write rows of
`x,y,z,name,category,color[,major]` with no header:

```
1332,1690,0,Britain Bank,bank,yellow
1336,1997,0,Britain Moongate,moongate,yellow,true
```

and convert:

```bash
python3 .claude/skills/uo-poi-data/csv_to_json.py places.csv     # writes places.json next to it
```

This converter has no facet column. On a multi-facet shard, add `"map": <n>` to each point in
the output, or write the JSON by hand.

### Pointing the client at it

Put the `.json` files in a directory of their own and set `poi_directory` in `settings.json` to
its absolute path. The data loads on the first lookup, so after changing it restart the client.
Then check it:

```
navrey> poi                 # categories and counts that loaded, or why nothing did
navrey> closest bank
navrey> findpoi moongate    # searches every facet and says when a match is on another one
```

To add a place you found on foot, read your position from `/tmp/cuostate.json` while standing on
it, add a row to your CSV, reconvert, and restart.

## Under the hood

For the curious. None of this is needed to use it.

- The command engine runs inside the client process, under `src/ClassicUO.Client/Agent/`. The
  rest of ClassicUO is touched in only a handful of places.
- Commands arrive by appending a line to `/tmp/cuocmd`. Replies and game narration go to
  `/tmp/cuolog`. The character's state is published continuously to `/tmp/cuostate.json` and the
  nearby world to `/tmp/cuoworld.json`. `navrey` is a thin front end over those files.
- Route planning is the fork's own, so it can open doors, but each step is taken exactly the way
  the game window takes it, so the shard sees normal movement.
- `cli/uo` is a small Python library for scripts: read state, send actions, wait for outcomes.
  The combat and navigation scripts under `.claude/skills/` are built on it.
- `CLAUDE.md` is the agent's operating manual and carries the details this README leaves out.

# Upstream ClassicUO

This fork tracks [ClassicUO](https://github.com/ClassicUO/ClassicUO), an open source implementation
of the Ultima Online Classic Client built on [FNA](https://github.com/FNA-XNA/FNA). Upstream's own
README, downloads and contribution notes live there.

# Legal

The code itself has been written using the following projects as a reference:

- [OrionUO](https://github.com/hotride/orionuo)
- [Razor](https://github.com/msturgill/razor)
- [UltimaXNA](https://github.com/ZaneDubya/UltimaXNA)
- [ServUO](https://github.com/servuo/servuo)

Backend:

- [FNA](https://github.com/FNA-XNA/FNA)

This work is released under the BSD 4 license. This project does not distribute any copyrighted game assets. In order to run this client you'll need to legally obtain a copy of the Ultima Online Classic Client.
Using a custom client to connect to official UO servers is strictly forbidden. We do not assume any responsibility of the usage of this client.

Ultima Online(R) © 2024 Electronic Arts Inc. All Rights Reserved.
