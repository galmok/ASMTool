#region License
/*
 * Copyright (C) 2019 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AsmTool
{
	class Program
	{
		static void Main(string[] args) {
			string cmd = args.Length > 0 ? args[0] : "flash_read";

			// File-only command: reads a ROM image directly, so it needs neither the
			// ASMedia driver nor elevated privileges.
			if (cmd == "rom_version") {
				RunRomVersion(args);
				return;
			}

			IAsmIO io = AsmIOFactory.GetAsmIO();

			Console.WriteLine("Unloading ASM Driver...");
			io.UnloadAsmIODriver();
			Console.WriteLine("Loading ASM Driver...");
			if (io.LoadAsmIODriver() != 1) {
				Console.Error.WriteLine("Failed to load ASM IO Driver");
				return;
			}

			bool usbOnly = (cmd == "fw_set_type" || cmd == "fw_info" || cmd == "mem_read");

			AsmSataDevice? sata = null;
			AsmDevice? usb = null;

			if (usbOnly) {
				usb = AsmDevice.TryCreate(io);
				if (usb == null) {
					Console.Error.WriteLine("No USB ASMedia controller (0x2142/0x3142) detected.");
					io.UnloadAsmIODriver();
					return;
				}
			} else {
				// flash_read / flash_info : prefer SATA (ASM116x), fall back to USB.
				sata = AsmSataDevice.TryCreate(io);
				if (sata == null) {
					usb = AsmDevice.TryCreate(io);
				}
				if (sata == null && usb == null) {
					Console.Error.WriteLine("No supported ASMedia controller detected (tried SATA and USB families).");
					io.UnloadAsmIODriver();
					return;
				}
			}

			try {
				switch (cmd) {
					case "flash_read":
						{
							string outName = args.Length > 1 ? args[1] : "dump.bin";
							if (sata != null) {
								Console.WriteLine("Dumping SATA SPI flash firmware...");
								sata.DumpFirmware(outName);
							} else {
								Console.WriteLine("Dumping USB firmware...");
								usb!.DumpFirmware(outName);
							}
							break;
						}
					case "flash_info":
						{
							if (sata == null) {
								Console.Error.WriteLine("flash_info is only supported on SATA (ASM116x) controllers.");
								return;
							}
							sata.Open();
							if (!sata.RequestGrant()) {
								Console.Error.WriteLine("Failed to obtain the SPI control grant.");
								return;
							}
							var chip = sata.DetectChip();
							if (chip == null) {
								Console.Error.WriteLine("Could not read the flash JEDEC ID.");
							} else {
								var c = chip.Value;
								Console.WriteLine($"Flash chip : {c.Name}");
								Console.WriteLine($"JEDEC ID   : {c.Jedec0:X2} {c.Jedec1:X2} {c.Jedec2:X2}");
								Console.WriteLine($"Capacity   : {c.CapacityKb} KB ({c.CapacityKb * 1024} bytes)");
								try {
									string? ver = sata.ReadFirmwareVersion(c.CapacityKb * 1024);
									Console.WriteLine($"Firmware   : {(ver ?? "(version block not found)")}");
								} catch (Exception ex) {
									Console.Error.WriteLine($"Firmware   : (read error: {ex.Message})");
								}
								Console.WriteLine($"BAR0 base  : 0x{sata.BaseAddress:X8}");
							}
							break;
						}
					case "fw_set_type":
						{
							if (args.Length < 3) {
								Console.Error.WriteLine("Usage: AsmTool fw_set_type <firmware.rom> <2142|3142>");
								return;
							}
							var dir = Path.GetDirectoryName(args[1]) ?? "";
							var patchedFile = Path.Combine(
								dir,
								Path.GetFileNameWithoutExtension(args[1]) + "_patched.bin"
							);
							File.Copy(args[1], patchedFile, true);
							using var fw = new AsmFirmware(patchedFile);
							var newType = args[2] == "2142"
								? AsmFirmwareChipType.Asm2142
								: AsmFirmwareChipType.Asm3142;
							Console.WriteLine($"Setting {newType}");
							fw.SetChipType(newType);
							break;
						}
					case "fw_info":
						{
							if (args.Length < 2) {
								Console.Error.WriteLine("Usage: AsmTool fw_info <firmware.rom>");
								return;
							}
							using var fw = new AsmFirmware(args[1]);
							fw.PrintInfo(usb!, Console.Out);
							break;
						}
					case "mem_read":
						usb!.DumpMemory();
						break;
					default:
						Console.Error.WriteLine($"Unknown command: {cmd}");
						PrintUsage();
						return;
				}
			} finally {
				sata?.Dispose();
				io.UnloadAsmIODriver();
			}
		}

		static void RunRomVersion(string[] args) {
			if (args.Length < 2) {
				Console.Error.WriteLine("Usage: AsmTool rom_version <firmware.rom>");
				return;
			}
			string file = args[1];
			if (!File.Exists(file)) {
				Console.Error.WriteLine($"File not found: {file}");
				return;
			}

			byte[] data = File.ReadAllBytes(file);
			string? ver = AsmSataFwVersion.Extract(data);
			if (ver == null) {
				Console.Error.WriteLine("Could not find the firmware version block (marker \"2116RAM\") in the ROM.");
				return;
			}
			Console.WriteLine($"Firmware version: {ver}");
		}

		static void PrintUsage() {
			Console.WriteLine("Usage:");
			Console.WriteLine("  AsmTool [flash_read [out.bin]]       Dump the SPI flash firmware (SATA or USB)");
			Console.WriteLine("  AsmTool flash_info                   Show the SATA flash chip / capacity / firmware version");
			Console.WriteLine("  AsmTool rom_version <firmware.rom>   Print the firmware version of a SATA ROM file (no driver needed)");
			Console.WriteLine("  AsmTool fw_info <firmware.rom>       Inspect a USB firmware image");
			Console.WriteLine("  AsmTool fw_set_type <rom> <2142|3142>  Patch the chip type of a USB firmware");
			Console.WriteLine("  AsmTool mem_read                     Dump 128 KB of USB controller memory to mem.bin");
		}
	}
}
