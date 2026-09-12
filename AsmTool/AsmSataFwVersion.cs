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
	/// The ROM carries a small identification block of the form:
	///     [2 pad bytes] [6-byte version] ["2116RAM" name]
	/// where the 6-byte version is a big-endian 48-bit value such as
	/// 0x241025000005, reported as "241025-0000-05" (YYMMDD-build-rev).
	/// The name marker "2116RAM" is unique within the image and anchors the
	/// version, so the location is found without relying on a fixed offset.
	/// </summary>
	public static class AsmSataFwVersion
	{
		/// <summary>Name marker that immediately follows the version field.</summary>
		public static readonly byte[] Anchor =
			{ 0x32, 0x31, 0x31, 0x36, 0x52, 0x41, 0x4D }; // "2116RAM"

		/// <summary>Number of version bytes preceding <see cref="Anchor"/>.</summary>
		public const int VersionLen = 6;

		/// <summary>
		/// Returns the formatted version (e.g. "241025-0000-05") or
		/// <c>null</c> when the version block is not present in <paramref name="data"/>.
		/// </summary>
		public static string? Extract(byte[] data) => Extract(data, data.Length);

		public static string? Extract(byte[] data, int length) {
			int anchor = IndexOf(data, length, Anchor);
			if (anchor < VersionLen) {
				return null;
			}

			ulong v = 0;
			for (int i = 0; i < VersionLen; i++) {
				v = (v << 8) | data[anchor - VersionLen + i];
			}
			return Format(v);
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
