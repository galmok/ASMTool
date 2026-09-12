#region License
/*
 * Copyright (C) 2026 Bernhard Ege <galmok@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using System;
using System.IO;

namespace AsmTool
{
	/// <summary>
	/// Firmware reader for ASMedia SATA controllers (ASM106x/ASM116x family, e.g. ASM1166).
	///
	/// The protocol is a faithful transcription of the SPI flash access sequences used by
	/// ASMedia's own RomUpdWin.exe (ASM116xMPTool). The SPI controller is exposed through a
	/// memory-mapped window (MapAsmIO) with the following registers relative to the window:
	///   +0xB00  SPI command/data port (DWORD)
	///   +0xB04  SPI status/control  (bit1 pre-op, bit4 post-op, bit5 busy)
	///   +0xB06  SPI grant           (bit0 request, bit1 granted)
	/// </summary>
	public class AsmSataDevice : IDisposable
	{
		const uint REG_WINDOW_OFFSET = 0x1000;
		const uint REG_WINDOW_SIZE = 0x1000;

		const uint REG_DATA = 0xB00;   // SPI command/data port
		const uint REG_STATUS = 0xB04; // SPI status/control
		const uint REG_GRANT = 0xB06;  // SPI grant
		const uint REG_MISC = 0xB11;   // initialised during open

		const byte READ_OPCODE = 0x03; // SPI Read Data (from the per-chip LUT, identical for all chips)
		const byte READ_ID_OPCODE = 0x9F;
		const byte READ_ID_FALLBACK_OPCODE = 0xAB;

		const int WAIT_BUSY_TIMEOUT_MS = 64;
		const int GRANT_POLL_TIMEOUT_TICKS = 3;   // units of (ms / 64)
		const int CHUNK_SIZE = 4096;

		public const uint VID_ASMEDIA = 0x1B21;
		/// <summary>
		/// ASM116x SATA controller PIDs supported by ASMedia's own RomUpdWin.exe
		/// (ASM116xMPTool device table @0x591B52).
		/// </summary>
		public static readonly uint[] SataPids = { 0x1062, 0x1064, 0x1164, 0x1165, 0x1166 };

		private readonly IAsmIO io;
		private readonly uint bus;
		private readonly uint dev;
		private readonly uint func;

		private uint baseAddr;
		private uint handle;
		private bool opened;
		private bool granted;

		public AsmSataDevice(IAsmIO io, PCIAddress addr) {
			this.io = io;
			this.bus = addr.Bus;
			this.dev = addr.Device;
			this.func = addr.Function;
		}

		/// <summary>
		/// Scans the PCI space for a supported ASM116x SATA controller and, if found,
		/// returns a ready-to-use device. Returns <c>null</c> when none is present.
		/// </summary>
		public static AsmSataDevice? TryCreate(IAsmIO io) {
			var prb = new Prober(io);
			if (prb.FindByProductList(SataPids, VID_ASMEDIA, out PCIAddress addr, out uint pid)) {
				Console.WriteLine($"Found ASM116x SATA controller (bus {addr.Bus:X}, dev {addr.Device:X}, func {addr.Function:X}, pid {pid:X4})");
				return new AsmSataDevice(io, addr);
			}
			return null;
		}

		public uint BaseAddress => baseAddr;

		/// <summary>
		/// Maps the SPI controller window and performs the initial handshake.
		/// </summary>
		public void Open() {
			if (opened) {
				return;
			}

			uint bar0 = io.PCI_Read_DWORD(bus, dev, func, 0x10);
			baseAddr = bar0 & 0xFFFFFFF0u;
			if (baseAddr == 0) {
				throw new Exception($"SATA controller BAR0 is not mapped (bus {bus:X}, dev {dev:X}, func {func:X})");
			}

			handle = io.MapAsmIO(baseAddr + REG_WINDOW_OFFSET, REG_WINDOW_SIZE);
			opened = true;

			WaitGrant();
			byte st = ReadReg(REG_MISC);
			WriteReg(REG_MISC, (byte)(st & 0xF1));
		}

		public void Dispose() {
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		private void Dispose(bool disposing) {
			if (!opened) {
				return;
			}
			try {
				if (granted) {
					ReleaseGrant();
					granted = false;
				}
			} finally {
				io.UnmapAsmIO(handle, REG_WINDOW_SIZE);
				opened = false;
			}
		}

		// ------------------------------------------------------------------
		// Raw register access
		// ------------------------------------------------------------------

		private unsafe byte ReadReg(uint reg) {
			byte* p = stackalloc byte[1];
			io.ReadMEM(handle + reg, 1, new IntPtr(p));
			return *p;
		}

		private unsafe void WriteReg(uint reg, byte value) {
			byte* p = stackalloc byte[1];
			*p = value;
			io.WriteMEM(handle + reg, 1, new IntPtr(p));
		}

		private byte ReadB04() => ReadReg(REG_STATUS);
		private void WriteB04(byte v) => WriteReg(REG_STATUS, v);
		private byte ReadB06() => ReadReg(REG_GRANT);
		private void WriteB06(byte v) => WriteReg(REG_GRANT, v);

		private unsafe uint ReadB00(int size) {
			uint* p = stackalloc uint[1];
			io.ReadMEM(handle + REG_DATA, (uint)size, new IntPtr(p));
			return *p;
		}

		private unsafe void WriteB00(byte[] data, int size) {
			fixed (byte* p = data) {
				io.WriteMEM(handle + REG_DATA, (uint)size, new IntPtr(p));
			}
		}

		// ------------------------------------------------------------------
		// Primitives (transcribed from RomUpdWin.exe)
		// ------------------------------------------------------------------

		/// <summary>
		/// Waits for the device grant by walking the PCI capability list starting at
		/// config offset 0x34 until a capability with ID 1 is found, then enables the
		/// PCI command register (memory space + bus master).
		/// (RomUpdWin.exe 0x40d2f0)
		/// </summary>
		private void WaitGrant() {
			byte offset = io.PCI_Read_BYTE(bus, dev, func, 0x34);

			while (true) {
				byte v = io.PCI_Read_BYTE(bus, dev, func, offset);
				if (v == 1) {
					offset = (byte)(offset + 4);
					break;
				}
				if (v == 0) {
					return;
				}
				offset++;
				v = io.PCI_Read_BYTE(bus, dev, func, offset);
				offset = v;
			}

			byte w = io.PCI_Read_BYTE(bus, dev, func, offset);
			if ((w & 3) == 0) {
				return;
			}
			io.PCI_Write_BYTE(bus, dev, func, offset, (byte)(w & 0xFC));
			io.PCI_Write_BYTE(bus, dev, func, 4, 6);
		}

		/// <summary>
		/// Waits until the SPI status busy bit (bit5) is clear, with a timeout.
		/// (RomUpdWin.exe 0x40d440)
		/// </summary>
		private bool WaitBusy() {
			int start = Environment.TickCount;
			while (true) {
				WaitGrant();
				byte st = ReadB04();
				if ((st & 0x20) == 0) {
					return true;
				}
				if ((Environment.TickCount - start) / 64 >= 1) {
					return false;
				}
			}
		}

		/// <summary>
		/// Requests the SPI control grant (grant bit0 set, poll bit1), up to 3 attempts.
		/// (RomUpdWin.exe 0x401d30)
		/// </summary>
		public bool RequestGrant() {
			if (granted) {
				return true;
			}
			for (int attempt = 0; attempt < 3; attempt++) {
				WaitGrant();
				byte st = ReadB06();
				WaitGrant();
				WriteB06((byte)(st | 0x01));

				int start = Environment.TickCount;
				bool ok = false;
				while (true) {
					WaitGrant();
					st = ReadB06();
					if ((st & 0x02) != 0) {
						ok = true;
						break;
					}
					if ((Environment.TickCount - start) / 64 >= GRANT_POLL_TIMEOUT_TICKS) {
						break;
					}
				}

				WaitGrant();
				st = ReadB06();
				WaitGrant();
				WriteB06((byte)(st & 0xFE));

				if (ok) {
					granted = true;
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Releases the SPI control grant and unmaps the window.
		/// (RomUpdWin.exe 0x401e30)
		/// </summary>
		private void ReleaseGrant() {
			WaitGrant();
			byte st = ReadB06();
			WaitGrant();
			WriteB06((byte)(st & 0xFE));
			io.UnmapAsmIO(handle, REG_WINDOW_SIZE);
		}

		/// <summary>
		/// Writes up to 4 bytes to the SPI data port.
		/// (RomUpdWin.exe 0x40d570)
		/// </summary>
		private bool WriteBurst(byte[] data, int size) {
			if (size > 4) {
				return false;
			}
			if (!WaitBusy()) {
				return false;
			}
			byte[] local = new byte[4];
			if (size > 0) {
				Array.Copy(data, 0, local, 0, size);
			}
			WaitGrant();
			WriteB00(local, 4);
			WaitGrant();
			ReadB04();
			WaitGrant();
			WriteB04((byte)((size & 7) | 0x28));
			WaitBusy();
			return true;
		}

		/// <summary>
		/// Reads up to 4 bytes from the SPI data port.
		/// (RomUpdWin.exe 0x40d4b0)
		/// </summary>
		private bool ReadBurst(byte[] buf, int size) {
			if (size > 4) {
				return false;
			}
			if (!WaitBusy()) {
				return false;
			}
			WaitGrant();
			byte st = ReadB04();
			WaitGrant();
			WriteB04((byte)((st & 7) | 0x20));
			if (!WaitBusy()) {
				return false;
			}
			WaitGrant();
			uint dw = ReadB00(4);
			if (size > 0) {
				buf[0] = (byte)dw;
				if (size > 1) buf[1] = (byte)(dw >> 8);
				if (size > 2) buf[2] = (byte)(dw >> 16);
				if (size > 3) buf[3] = (byte)(dw >> 24);
			}
			return true;
		}

		/// <summary>
		/// Reads the 3-byte JEDEC ID from the flash.
		/// (RomUpdWin.exe 0x40d630)
		/// </summary>
		public byte[] ReadId() {
			byte[] id = new byte[3];

			WaitGrant();
			byte st = ReadB04();
			WaitGrant();
			WriteB04((byte)(st & 0xEF));

			if (WaitBusy()) {
				WaitGrant();
				WriteB00(new byte[] { READ_ID_OPCODE, 0, 0, 0 }, 4);
				WaitGrant();
				ReadB04();
				WaitGrant();
				WriteB04(0x29);
				WaitBusy();
			}

			if (WaitBusy()) {
				WaitGrant();
				ReadB04();
				WaitGrant();
				WriteB04(0x23);
				if (WaitBusy()) {
					WaitGrant();
					uint dw = ReadB00(4);
					id[0] = (byte)dw;
					id[1] = (byte)(dw >> 8);
					id[2] = (byte)(dw >> 16);
				}
			}

			WaitGrant();
			st = ReadB04();
			WaitGrant();
			WriteB04((byte)(st | 0x10));

			return id;
		}

		/// <summary>
		/// Reads <paramref name="size"/> bytes of flash starting at <paramref name="offset"/>.
		/// (RomUpdWin.exe 0x40e0a0)
		/// </summary>
		public void ReadRegion(uint offset, int size, byte[] buf) {
			if (size == 0) {
				return;
			}
			WaitGrant();
			byte st = ReadB04();
			WaitGrant();
			WriteB04((byte)(st & 0xEF));

			byte[] cmd = new byte[] {
				READ_OPCODE,
				(byte)(offset >> 16),
				(byte)(offset >> 8),
				(byte)offset
			};
			WriteBurst(cmd, 4);

			int remaining = size;
			int pos = 0;
			byte[] scratch = new byte[4];
			while (remaining > 0) {
				int n = Math.Min(4, remaining);
				if (!ReadBurst(scratch, n)) {
					throw new Exception($"SATA flash read failed at offset {offset + (uint)pos:X}");
				}
				Array.Copy(scratch, 0, buf, pos, n);
				pos += n;
				remaining -= n;
			}

			WaitGrant();
			st = ReadB04();
			WaitGrant();
			WriteB04((byte)(st | 0x10));
		}

		// ------------------------------------------------------------------
		// High level
		// ------------------------------------------------------------------

		/// <summary>
		/// Detects the SPI flash chip by reading its JEDEC ID (with the 0xAB fallback)
		/// and looking it up in the known-chip table.
		/// (RomUpdWin.exe 0x40e580)
		/// </summary>
		public AsmSataChip? DetectChip() {
			byte[] id = ReadId();
			bool bad = (id[0] == id[1] && id[1] == id[2]) && (id[0] == 0xFF || id[0] == 0x00);

			if (bad) {
				// Fallback: 0xAB sequence
				WaitGrant();
				byte st6 = ReadB06();
				WaitGrant();
				WriteB06((byte)(st6 & 0xEF));

				WaitBusy();
				WriteB00(new byte[] { READ_ID_FALLBACK_OPCODE, 0, 0, 0 }, 4);
				WaitGrant();

				byte[] id2 = new byte[3];
				ReadBurst(id2, 3);

				WaitGrant();
				st6 = ReadB06();
				WaitGrant();
				WriteB06((byte)(st6 | 0x10));

				id = id2;
			}

			bool stillBad = (id[0] == id[1] && id[1] == id[2]) && (id[0] == 0xFF || id[0] == 0x00);
			if (stillBad) {
				return null;
			}

			return AsmSataChipTable.Find(id[0], id[1], id[2])
				?? new AsmSataChip {
					Jedec0 = id[0],
					Jedec1 = id[1],
					Jedec2 = id[2],
					CapacityKb = AsmSataChipTable.UnknownCapacityKb,
					Name = "Unknown"
				};
		}

		/// <summary>
		/// Dumps the entire SPI flash to <paramref name="filename"/>.
		/// </summary>
		public void DumpFirmware(string filename, int? sizeOverride = null) {
			if (!opened) {
				Open();
			}
			if (!granted) {
				if (!RequestGrant()) {
					throw new Exception("Failed to obtain the SPI control grant");
				}
			}

			AsmSataChip? chip = DetectChip();
			int size = sizeOverride ?? (chip?.CapacityKb ?? AsmSataChipTable.UnknownCapacityKb) * 1024;
			if (chip != null) {
				var c = chip.Value;
				Console.WriteLine($"Flash chip : {c.Name}");
				Console.WriteLine($"JEDEC ID   : {c.Jedec0:X2} {c.Jedec1:X2} {c.Jedec2:X2}");
				Console.WriteLine($"Capacity   : {c.CapacityKb} KB ({size} bytes)");
			} else {
				Console.WriteLine($"Chip not recognised; dumping default {size} bytes");
			}

			byte[] chunk = new byte[CHUNK_SIZE];
			using (var fs = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.Read)) {
				uint offset = 0;
				while (offset < (uint)size) {
					int n = Math.Min(CHUNK_SIZE, size - (int)offset);
					ReadRegion(offset, n, chunk);
					fs.Write(chunk, 0, n);
					offset += (uint)n;
				}
			}
		}
	}
}
