#region License
/*
 * Copyright (C) 2026 Bernhard Ege <galmok@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
namespace AsmTool
{
	/// <summary>
	/// Extracts the firmware version string from an ASM116x SATA ROM image.
	///
	/// The ROM is an ASMedia "ASMT" image that wraps a standard PE32+ firmware
	/// executable. It carries a 48-bit (big-endian) version value such as
	/// 0x241025000005, reported as "241025-0000-05" (YYMMDD-build-rev), stored
	/// in a descriptor block that is immediately followed by the name marker
	/// "2116RAM".
	///
	/// Two methods locate the version, tried in order:
	///   1. Preferred (O(1)): the ASMT header. The 4 bytes at offset 0x100 are
	///      the magic "ASMT"; the 32-bit little-endian field at offset 0x0 is the
	///      base of the descriptor block; the version is at base + 0x10. (The same
	///      base offset is redundantly stored big-endian at 0x114.)
	///   2. Fallback (brute force): search for the unique "2116RAM" name marker and
	///      read the 6 bytes that precede it. This is layout-independent and works
	///      even when the header is absent or has moved.
	/// </summary>
	public static class AsmSataFwVersion
	{
		/// <summary>Name marker that immediately follows the version field.</summary>
		public static readonly byte[] Anchor =
			{ 0x32, 0x31, 0x31, 0x36, 0x52, 0x41, 0x4D }; // "2116RAM"

		/// <summary>Number of version bytes.</summary>
		public const int VersionLen = 6;

		/// <summary>ASMT magic, located at <see cref="AsmtMagicOffset"/>.</summary>
		public static readonly byte[] AsmtMagic =
			{ 0x41, 0x53, 0x4D, 0x54 }; // "ASMT"

		/// <summary>Offset of the ASMT magic in the ROM header.</summary>
		public const int AsmtMagicOffset = 0x100;

		/// <summary>
		/// The version is at (descriptor base from field 0x0) + this delta.
		/// </summary>
		public const int AsmtVersionDelta = 0x10;

		/// <summary>
		/// Returns the formatted version (e.g. "241025-0000-05") or
		/// <c>null</c> when the version cannot be located in <paramref name="data"/>.
		/// </summary>
		public static string? Extract(byte[] data) => Extract(data, data.Length);

		public static string? Extract(byte[] data, int length) {
			// 1) Preferred: the ASMT header carries an authoritative base offset.
			if (TryAsmtHeader(data, length, out uint versionBase)) {
				long vstart = versionBase + AsmtVersionDelta;
				if (vstart + VersionLen <= length) {
					return Format(ReadVersion(data, (int)vstart));
				}
			}

			// 2) Fallback: brute-force search for the name marker.
			int anchor = IndexOf(data, length, Anchor);
			if (anchor >= VersionLen) {
				return Format(ReadVersion(data, anchor - VersionLen));
			}
			return null;
		}

		/// <summary>
		/// If <paramref name="data"/> begins with an ASMT ROM header (magic "ASMT"
		/// at <see cref="AsmtMagicOffset"/>), returns <c>true</c> and outputs the
		/// descriptor-block base offset from the little-endian field at 0x0;
		/// otherwise returns <c>false</c>. <paramref name="data"/> must be at least
		/// 0x104 bytes.
		/// </summary>
		public static bool TryAsmtHeader(byte[] data, int length, out uint versionBase) {
			versionBase = 0;
			if (length < AsmtMagicOffset + 4) {
				return false;
			}
			for (int i = 0; i < AsmtMagic.Length; i++) {
				if (data[AsmtMagicOffset + i] != AsmtMagic[i]) {
					return false;
				}
			}
			versionBase = (uint)(data[0]
				| (uint)(data[1] << 8)
				| (uint)(data[2] << 16)
				| (uint)(data[3] << 24));
			return true;
		}

		/// <summary>Reads the 48-bit big-endian version at <paramref name="offset"/>.</summary>
		public static ulong ReadVersion(byte[] data, int offset) {
			ulong v = 0;
			for (int i = 0; i < VersionLen; i++) {
				v = (v << 8) | data[offset + i];
			}
			return v;
		}

		/// <summary>Formats a 48-bit version value as "XXXXXX-XXXX-XX".</summary>
		public static string Format(ulong v) {
			string h = v.ToString("X").PadLeft(12, '0');
			return h.Substring(0, 6) + "-" + h.Substring(6, 4) + "-" + h.Substring(10, 2);
		}

		/// <summary>
		/// Finds the first occurrence of <paramref name="pattern"/> within the first
		/// <paramref name="length"/> bytes of <paramref name="data"/>; -1 when absent.
		/// </summary>
		public static int IndexOf(byte[] data, int length, byte[] pattern) {
			if (pattern.Length == 0) {
				return 0;
			}
			int limit = length - pattern.Length;
			for (int i = 0; i <= limit; i++) {
				int j = 0;
				while (j < pattern.Length && data[i + j] == pattern[j]) {
					j++;
				}
				if (j == pattern.Length) {
					return i;
				}
			}
			return -1;
		}
	}
}
