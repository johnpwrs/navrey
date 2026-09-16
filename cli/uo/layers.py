"""Equipment layers, transcribed from src/ClassicUO.Client/Game/Data/Layers.cs.

`equip` takes the hex value. Note 0x0B is Hair, not a second ring slot - mistaking it for stuck
equipment has caused real confusion when checking what a character is wearing.
"""

from enum import IntEnum


class Layer(IntEnum):
    INVALID = 0x00
    ONE_HANDED = 0x01
    TWO_HANDED = 0x02
    SHOES = 0x03
    PANTS = 0x04
    SHIRT = 0x05
    HELMET = 0x06
    GLOVES = 0x07
    RING = 0x08
    TALISMAN = 0x09
    NECKLACE = 0x0A
    HAIR = 0x0B
    WAIST = 0x0C
    TUNIC = 0x0D
    BRACELET = 0x0E
    FACE = 0x0F
    BEARD = 0x10
    TORSO = 0x11
    EARRINGS = 0x12
    ARMS = 0x13
    CLOAK = 0x14
    BACKPACK = 0x15
    ROBE = 0x16
    SKIRT = 0x17
    LEGS = 0x18
    MOUNT = 0x19
    SHOP_BUY_RESTOCK = 0x1A
    SHOP_BUY = 0x1B
    SHOP_SELL = 0x1C
    BANK = 0x1D

    @property
    def hex(self) -> str:
        """The two-digit form `equip` expects."""
        return f"{self.value:02X}"
