---
name: uo-sell-items
description: Sell items to an NPC vendor with the UO client — open their sell list with the "<Name> sell" speech command, see what they will buy out of your backpack, and sell it. Use whenever the user asks to sell loot, offload items, turn gear into gold, or asks who buys a particular item.
---

# Selling to an NPC vendor

Requires a logged-in character (`uo-login`) standing next to the vendor. The mirror of
`uo-buy-items`, but the differences matter more than the similarities — read the "not the same as
buying" section before assuming anything carries over.

## The three steps

```bash
echo "say <Name> sell" >> /tmp/cuocmd    # 1. open the sell list - their NAME, not "vendor"
sleep 2
echo "selllist" >> /tmp/cuocmd           # 2. what they will buy, and for how much
sleep 1
tail -15 /tmp/cuolog
echo "sell 0x4086BBF8 1" >> /tmp/cuocmd  # 3. sell one, by the serial `selllist` printed
```

Step 3 takes **as many `<serial> <amount>` pairs as you like** — the 0x9F sell packet carries a
list, so a whole pack of loot goes in one request rather than one round trip per stack:

```bash
echo "sell 0x4086F451 1 0x4086F3EA 1 0x4086F427 1 0x4086F3D4 1 0x4086F436 1" >> /tmp/cuocmd
# <Name>: Thank you! I bought 5 items.  Here is your 91gp.
```

**But the server caps one sell at 5 lines.** A six-line request is refused whole — `You may only
sell 5 items at a time!` — and nothing sells, so chunk longer lists into batches of five. The cap
is on *lines*, not units: a line's amount can be the whole stack.

A successful sale is a bark plus the gold landing:

```
[SHOP] Sell list from 0x0028D618: 1 entries - use 'selllist' to list
  0x4086AE05       4gp  x1    bandana
<Name>: Thank you! I bought 1 item.  Here is your 4gp.
```

Confirm with the state file rather than the bark — `jq .gold /tmp/cuostate.json`.

## Reading the vendor's answer

The vendor tells you which case you are in, and the three are easy to confuse:

| What you see | What it means |
|---|---|
| `You have nothing I would be interested in.` | The speech reached the server and it checked your pack. This vendor genuinely buys none of what you are carrying. Not an error, and not worth retrying. |
| `[SHOP] Sell list from 0x… : N entries` | It wants something. Run `selllist`, then `sell`. |
| Nothing at all | The speech never reached the server (wrong name, or too far away). Get adjacent and check the name with `click <serial>`. |

## Not the same as buying — three differences that bite

- **The serials are *your* items, not the vendor's stock.** A buy-list serial identifies something
  the vendor owns; a sell-list serial identifies an item in your own backpack. They are stored
  separately for exactly this reason, and a `sell` that reached for the buy list would hand the
  vendor its own stock serials back.
- **The sell list does NOT close after a sale.** Buying does — a second `buy` without re-saying
  `<Name> buy` gets "Thou hast bought nothing!", and reopening mints fresh serials for every line.
  Selling has neither problem: two `sell` commands in a row against one `<Name> sell` both
  succeeded, and the serials stayed valid because they are your items and did not move. Verified
  live; don't copy the buy-side reopen loop here.
- **Both take a cart, but the caps differ.** `buy` and `sell` each accept any number of
  `<serial> <amount>` pairs in one command. A single `buy` line is capped at 20 units; `sell` has
  no per-line unit cap we have hit, but it *is* capped at **5 lines per request** (measured — see
  above), which `buy` is not.

## Vendors buy at about half what they charge

Bought a bandana for 8gp, sold it straight back for 4gp. Treat the spread as the cost of using a
vendor at all, and don't plan around buying low from one NPC to sell high to another. The item you
sell also goes **into that vendor's stock** — the bandana reappeared in that vendor's buy list at
7gp moments later.

## Who buys what

Vendors only buy inside their own trade, so the answer to "it says nothing I would be interested
in" is usually "right shop, wrong trade":

| Vendor | Buys |
|---|---|
| Blacksmith / weaponsmith / armourer | metal weapons and metal armor |
| Tanner / leather worker | leather and **studded** armor |
| Tailor | cloth goods, cloth, bolts |
| Provisioner | general goods |

**Some loot categories have no buyer on a given shard at all.** Bone armor is the classic case
(measured on one shard: two blacksmiths both replied "nothing I would be interested in" to a pack
full of it, and the shard's own community confirmed nobody buys it) - shards differ, so test once
with the item in your pack rather than assuming either way. When a category a grind produces a lot
of turns out to be unsellable, it is **bank-or-drop, not income**. `rib cage`, `bone pile` and body
parts are pure corpse junk and no one takes them anywhere.

Finished garments cannot be turned into cloth to sell as raw material, either — scissors on a robe
or a fancy shirt both give "Scissors cannot be used on that to produce anything." Only cloth and
bolts cut, and they cut into bandages (see `uo-buy-items`).

## How this is wired, and what it looked like when it was not

`Send_SellRequest` (`0x9F`) always existed in the client, and ClassicUO has always handled the
server's sell list (`0x9E`) by building a `ShopGump`. But a `ShopGump` is private to the UI, so a
headless caller could see none of it. The agent side now records the list as the Narrator sees the
packet go past — `Capabilities/VendorStock.ReplaceSell`, read back by `selllist` — exactly the way
`0x74` is captured for buying.

Worth recognising the old symptom, because it was genuinely confusing: **the vendor went silent.**
With nothing sellable in the pack it said "you have nothing I would be interested in"; the moment
the pack contained something it *did* want, that reply stopped and nothing replaced it. The server
had sent the sell list and nothing was listening. Silence meant success, which is the worst
possible signal. If selling ever goes quiet again, that is the shape of the bug — check that a
`[SHOP] Sell list from …` line appears at all.
