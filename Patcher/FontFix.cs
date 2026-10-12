using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Patcher
{
	/// <summary>
	/// The app's TT Norms fonts carry Apple AAT layout tables (morx/feat/prop). The Android versions the app was
	/// built for ignored them; current Android's text shaper applies them, which switches on alternate glyphs and
	/// makes letters such as "c" look odd. Removing just those tables restores the original rendering; glyphs and
	/// kerning (the kern table) are untouched.
	/// </summary>
	internal static class FontFix
	{
		public static readonly string[] RemovedTables = new string[] { "morx", "feat", "prop" };

		/// <summary>Writes a fixed copy of every res/font/*.otf in the APK to outDir (same file names).</summary>
		public static List<string> FixApkFonts(string apk, string outDir)
		{
			Directory.CreateDirectory(outDir);
			List<string> list = new List<string>();
			using (ZipArchive zipArchive = ZipFile.OpenRead(apk))
			{
				foreach (ZipArchiveEntry zipArchiveEntry in zipArchive.Entries.Where((ZipArchiveEntry e) => e.FullName.StartsWith("res/font/") && e.FullName.EndsWith(".otf")))
				{
					byte[] array;
					using (Stream stream = zipArchiveEntry.Open())
					{
						using (MemoryStream memoryStream = new MemoryStream())
						{
							stream.CopyTo(memoryStream);
							array = memoryStream.ToArray();
						}
					}
					List<string> removed;
					byte[] bytes = FontFix.StripTables(array, FontFix.RemovedTables, out removed);
					File.WriteAllBytes(Path.Combine(outDir, zipArchiveEntry.Name), bytes);
					Console.WriteLine(string.Format("Fixed font {0}: removed {1}", zipArchiveEntry.FullName, (removed.Count > 0) ? string.Join(", ", removed) : "nothing"));
					list.Add(zipArchiveEntry.FullName);
				}
			}
			return list;
		}

		/// <summary>Rebuilds an sfnt (OpenType/TrueType) font without the given tables, with valid checksums.</summary>
		public static byte[] StripTables(byte[] font, string[] tags, out List<string> removed)
		{
			removed = new List<string>();
			int num = (int)FontFix.ReadUInt16(font, 4);
			List<FontFix.Table> list = new List<FontFix.Table>();
			for (int i = 0; i < num; i++)
			{
				int num2 = 12 + 16 * i;
				FontFix.Table table = new FontFix.Table
				{
					Tag = Encoding.ASCII.GetString(font, num2, 4),
					Checksum = FontFix.ReadUInt32(font, num2 + 4),
					Offset = (int)FontFix.ReadUInt32(font, num2 + 8),
					Length = (int)FontFix.ReadUInt32(font, num2 + 12)
				};
				if (tags.Contains(table.Tag))
				{
					removed.Add(table.Tag);
				}
				else
				{
					list.Add(table);
				}
			}
			int count = list.Count;
			int num3 = 1;
			int num4 = 0;
			while (num3 * 2 <= count)
			{
				num3 *= 2;
				num4++;
			}
			MemoryStream memoryStream = new MemoryStream();
			BinaryWriter binaryWriter = new BinaryWriter(memoryStream);
			binaryWriter.Write(font, 0, 4);
			FontFix.WriteUInt16(binaryWriter, (ushort)count);
			FontFix.WriteUInt16(binaryWriter, (ushort)(num3 * 16));
			FontFix.WriteUInt16(binaryWriter, (ushort)num4);
			FontFix.WriteUInt16(binaryWriter, (ushort)(count * 16 - num3 * 16));
			// Table records stay sorted by tag (they already are); table data keeps its original order.
			long position = memoryStream.Position;
			memoryStream.Position += (long)(16 * count);
			int num5 = -1;
			foreach (FontFix.Table table2 in list.OrderBy((FontFix.Table t) => t.Offset))
			{
				table2.NewOffset = (int)memoryStream.Position;
				byte[] array = new byte[table2.Length];
				Buffer.BlockCopy(font, table2.Offset, array, 0, table2.Length);
				if (table2.Tag == "head")
				{
					// checkSumAdjustment is recomputed below; it counts as zero for the table checksum.
					Array.Clear(array, 8, 4);
					num5 = table2.NewOffset;
				}
				table2.Checksum = FontFix.Checksum(array);
				binaryWriter.Write(array);
				while (memoryStream.Position % 4L != 0L)
				{
					binaryWriter.Write((byte)0);
				}
			}
			memoryStream.Position = position;
			foreach (FontFix.Table table3 in list)
			{
				binaryWriter.Write(Encoding.ASCII.GetBytes(table3.Tag));
				FontFix.WriteUInt32(binaryWriter, table3.Checksum);
				FontFix.WriteUInt32(binaryWriter, (uint)table3.NewOffset);
				FontFix.WriteUInt32(binaryWriter, (uint)table3.Length);
			}
			binaryWriter.Flush();
			byte[] array2 = memoryStream.ToArray();
			if (num5 >= 0)
			{
				// Per the OpenType spec: checkSumAdjustment = 0xB1B0AFBA - checksum of the whole font.
				uint value = 0xB1B0AFBAU - FontFix.Checksum(array2);
				array2[num5 + 8] = (byte)(value >> 24);
				array2[num5 + 9] = (byte)(value >> 16);
				array2[num5 + 10] = (byte)(value >> 8);
				array2[num5 + 11] = (byte)value;
			}
			return array2;
		}

		private static uint Checksum(byte[] data)
		{
			uint num = 0U;
			for (int i = 0; i < data.Length; i += 4)
			{
				uint num2 = 0U;
				for (int j = 0; j < 4; j++)
				{
					num2 = (num2 << 8 | (uint)((i + j < data.Length) ? data[i + j] : 0));
				}
				num += num2;
			}
			return num;
		}

		private static ushort ReadUInt16(byte[] b, int o)
		{
			return (ushort)((int)b[o] << 8 | (int)b[o + 1]);
		}

		private static uint ReadUInt32(byte[] b, int o)
		{
			return (uint)((int)b[o] << 24 | (int)b[o + 1] << 16 | (int)b[o + 2] << 8 | (int)b[o + 3]);
		}

		private static void WriteUInt16(BinaryWriter w, ushort v)
		{
			w.Write((byte)(v >> 8));
			w.Write((byte)v);
		}

		private static void WriteUInt32(BinaryWriter w, uint v)
		{
			w.Write((byte)(v >> 24));
			w.Write((byte)(v >> 16));
			w.Write((byte)(v >> 8));
			w.Write((byte)v);
		}

		private class Table
		{
			public string Tag;

			public uint Checksum;

			public int Offset;

			public int Length;

			public int NewOffset;
		}
	}
}
