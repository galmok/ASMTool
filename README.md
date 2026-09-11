# ASMTool
Firmware dumper and various utilities for PCI based ASMedia USB and SATA Controllers

It looks like all ICs in the ASM1x4x, ASM2x4x and ASM3x4x family use the same interface and share the same registers, but i only tested this with the ASM2142 Controller that i have in my system.

SATA controllers of the ASM106x/ASM116x family (e.g. the ASM1166) are also supported. The tool auto-detects the controller family, reads the SPI flash chip, and dumps the full flash contents.

# Why?
I'm having issues with my ASM2142 controller (lockup with USB 3.1 and large transfers), and i couldn't find a way to dump the current firmware.

The firmware updater can internally read the firmware, but it doesn't offer a way to save it.

# How to use
## Linux
```gcc -shared -o libAsmIoLinux.so -fPIC Linux/AsmIOLinux.c```
Place the resulting `.so` file next to the ASMTool executable (obtained by building this project)

## Windows
You'll need `AsmIo.sys` (for 32bit Windows) or `AsmIo64.sys` (for 64bit Windows).

You will also need `asmiodll.dll`. You can find these files if you google `ASM2142 firmware`.
Download the firmware updater and you'll find the files in there.

For SATA controllers (ASM116x) the `asmiodll.dll` / `AsmIo.sys` shipped with the ASMedia ASM116x firmware updater (`RomUpdWin`) also work and additionally provide the SATA flash access.

Place all files next to the ASMTool executable (obtained by building this project)

## Commands

Run the executable with a command. The controller family (SATA / USB) is auto-detected.

| Command | Description |
|---------|-------------|
| `flash_read [out.bin]` | Dump the SPI flash firmware. Works on both SATA (ASM116x) and USB (ASM2142/3142) controllers. Defaults to `dump.bin`. |
| `flash_info` | Print the detected SATA flash chip (name, JEDEC ID, capacity). SATA (ASM116x) only. |
| `fw_info <firmware.rom>` | Inspect a USB firmware image. |
| `fw_set_type <firmware.rom> <2142\|3142>` | Patch the chip type of a USB firmware image. |
| `mem_read` | Dump 128 KB of USB controller memory to `mem.bin`. |

Examples:

```
# Detect the flash chip on a SATA controller
ASMTool flash_info

# Dump the full SPI flash from a SATA controller
ASMTool flash_read sata.bin

# Dump the firmware from a USB controller
ASMTool flash_read usb_dump.bin
```

# How to contribute?
You can either extend this program and add new functionality, or

open a new Issue and attach the firmware obtained by running this program, so that other users can update their firmwares or try older versions to see if they work better

# They are custom Intel 8051 cores!
It turns out ASMedia USB controllers are custom Intel 8051 cores, and the firmware file can be disassembled into i8051 assembly

# Security implications
It looks like this interface could be used to flash malicious code onto ASMedia chips, as explained by
https://chefkochblog.wordpress.com/2018/03/19/asmedia-usb-3-x-controller-with-keylogger-and-malware-risks/

The chip performs no signature checks on the code being flashed and, being a PCIe device, could abuse DMA to read and write arbitrary memory
