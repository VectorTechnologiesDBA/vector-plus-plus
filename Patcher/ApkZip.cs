using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Patcher
{
	/// <summary>
	/// Minimal APK (zip) rewriter. Entries are copied without recompressing, so everything not explicitly
	/// replaced stays byte-identical. Stored entries are aligned like zipalign: Xamarin maps the stored
	/// assemblies/*.dll straight out of the APK.
	/// </summary>
	internal static class ApkZip
	{
		internal class Entry
		{
			public string Name;
			public byte[] NameBytes;
			public ushort VersionMadeBy;
			public ushort VersionNeeded;
			public ushort Flags;
			public ushort Method;
			public ushort Time;
			public ushort Date;
			public uint Crc32;
			public uint CompressedSize;
			public uint UncompressedSize;
			public byte[] CentralExtra;
			public byte[] Comment;
			public ushort InternalAttributes;
			public uint ExternalAttributes;
			public long LocalHeaderOffset;
			public long DataOffset;
			// Set for replaced or added entries.
			public byte[] NewData;
		}

		public static bool IsV1SignatureFile(string name)
		{
			if (!name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) || name.IndexOf('/', 9) >= 0)
			{
				return false;
			}
			string text = name.Substring(9).ToUpperInvariant();
			return text == "MANIFEST.MF" || text.EndsWith(".SF") || text.EndsWith(".RSA") || text.EndsWith(".DSA") || text.EndsWith(".EC");
		}

		public static int AlignmentFor(string name)
		{
			if (!name.EndsWith(".so", StringComparison.Ordinal))
			{
				return 4;
			}
			return 4096;
		}

		/// <summary>
		/// Writes dst from src: entries for which drop() is true are removed, entries in replace are written
		/// stored (uncompressed) with the given contents (added at the end if new), the rest are copied as-is.
		/// </summary>
		public static void Rewrite(string src, string dst, IDictionary<string, byte[]> replace, Func<string, bool> drop)
		{
			List<Entry> list = ApkZip.ReadEntries(src).Where((Entry e) => !drop(e.Name)).ToList();
			Dictionary<string, byte[]> dictionary = new Dictionary<string, byte[]>(replace);
			foreach (Entry entry in list)
			{
				byte[] array;
				if (dictionary.TryGetValue(entry.Name, out array))
				{
					ApkZip.SetStoredData(entry, array);
					dictionary.Remove(entry.Name);
				}
			}
			foreach (KeyValuePair<string, byte[]> keyValuePair in dictionary)
			{
				Entry entry2 = new Entry
				{
					Name = keyValuePair.Key,
					NameBytes = Encoding.UTF8.GetBytes(keyValuePair.Key),
					VersionMadeBy = 20,
					CentralExtra = new byte[0],
					Comment = new byte[0]
				};
				ApkZip.SetDosTime(entry2, DateTime.Now);
				ApkZip.SetStoredData(entry2, keyValuePair.Value);
				list.Add(entry2);
			}
			string text = dst + ".tmp";
			using (FileStream fileStream = File.OpenRead(src))
			{
				using (FileStream fileStream2 = File.Create(text))
				{
					BinaryWriter binaryWriter = new BinaryWriter(fileStream2);
					List<long> list2 = new List<long>();
					foreach (Entry entry3 in list)
					{
						long position = fileStream2.Position;
						list2.Add(position);
						byte[] array2 = new byte[0];
						if (entry3.Method == 0)
						{
							array2 = ApkZip.AlignmentExtra(position + 30L + (long)entry3.NameBytes.Length, ApkZip.AlignmentFor(entry3.Name));
						}
						// Regenerated local header: sizes and CRC come from the central directory, so no data descriptor is needed.
						binaryWriter.Write(ApkZip.kLocalHeaderSignature);
						binaryWriter.Write(entry3.VersionNeeded);
						binaryWriter.Write((ushort)(entry3.Flags & ~ApkZip.kDataDescriptorFlag));
						binaryWriter.Write(entry3.Method);
						binaryWriter.Write(entry3.Time);
						binaryWriter.Write(entry3.Date);
						binaryWriter.Write(entry3.Crc32);
						binaryWriter.Write(entry3.CompressedSize);
						binaryWriter.Write(entry3.UncompressedSize);
						binaryWriter.Write((ushort)entry3.NameBytes.Length);
						binaryWriter.Write((ushort)array2.Length);
						binaryWriter.Write(entry3.NameBytes);
						binaryWriter.Write(array2);
						if (entry3.NewData != null)
						{
							binaryWriter.Write(entry3.NewData);
						}
						else
						{
							fileStream.Position = entry3.DataOffset;
							ApkZip.CopyBytes(fileStream, fileStream2, (long)((ulong)entry3.CompressedSize));
						}
					}
					long position2 = fileStream2.Position;
					for (int i = 0; i < list.Count; i++)
					{
						Entry entry4 = list[i];
						binaryWriter.Write(ApkZip.kCentralHeaderSignature);
						binaryWriter.Write(entry4.VersionMadeBy);
						binaryWriter.Write(entry4.VersionNeeded);
						binaryWriter.Write((ushort)(entry4.Flags & ~ApkZip.kDataDescriptorFlag));
						binaryWriter.Write(entry4.Method);
						binaryWriter.Write(entry4.Time);
						binaryWriter.Write(entry4.Date);
						binaryWriter.Write(entry4.Crc32);
						binaryWriter.Write(entry4.CompressedSize);
						binaryWriter.Write(entry4.UncompressedSize);
						binaryWriter.Write((ushort)entry4.NameBytes.Length);
						binaryWriter.Write((ushort)entry4.CentralExtra.Length);
						binaryWriter.Write((ushort)entry4.Comment.Length);
						binaryWriter.Write((ushort)0);
						binaryWriter.Write(entry4.InternalAttributes);
						binaryWriter.Write(entry4.ExternalAttributes);
						binaryWriter.Write(ApkZip.ToUInt32(list2[i], "local header offset"));
						binaryWriter.Write(entry4.NameBytes);
						binaryWriter.Write(entry4.CentralExtra);
						binaryWriter.Write(entry4.Comment);
					}
					long num = fileStream2.Position - position2;
					binaryWriter.Write(ApkZip.kEndOfCentralDirectorySignature);
					binaryWriter.Write((ushort)0);
					binaryWriter.Write((ushort)0);
					binaryWriter.Write(ApkZip.ToUInt16(list.Count));
					binaryWriter.Write(ApkZip.ToUInt16(list.Count));
					binaryWriter.Write(ApkZip.ToUInt32(num, "central directory size"));
					binaryWriter.Write(ApkZip.ToUInt32(position2, "central directory offset"));
					binaryWriter.Write((ushort)0);
					binaryWriter.Flush();
				}
			}
			File.Move(text, dst, true);
		}

		public static List<Entry> ReadEntries(string path)
		{
			List<Entry> list = new List<Entry>();
			using (FileStream fileStream = File.OpenRead(path))
			{
				BinaryReader binaryReader = new BinaryReader(fileStream);
				long num = ApkZip.FindEndOfCentralDirectory(fileStream);
				fileStream.Position = num + 10L;
				int num2 = (int)binaryReader.ReadUInt16();
				binaryReader.ReadUInt32();
				fileStream.Position = (long)((ulong)binaryReader.ReadUInt32());
				for (int i = 0; i < num2; i++)
				{
					if (binaryReader.ReadUInt32() != ApkZip.kCentralHeaderSignature)
					{
						throw new InvalidDataException("bad central directory entry in " + path);
					}
					Entry entry = new Entry();
					entry.VersionMadeBy = binaryReader.ReadUInt16();
					entry.VersionNeeded = binaryReader.ReadUInt16();
					entry.Flags = binaryReader.ReadUInt16();
					entry.Method = binaryReader.ReadUInt16();
					entry.Time = binaryReader.ReadUInt16();
					entry.Date = binaryReader.ReadUInt16();
					entry.Crc32 = binaryReader.ReadUInt32();
					entry.CompressedSize = binaryReader.ReadUInt32();
					entry.UncompressedSize = binaryReader.ReadUInt32();
					int count = (int)binaryReader.ReadUInt16();
					int count2 = (int)binaryReader.ReadUInt16();
					int count3 = (int)binaryReader.ReadUInt16();
					binaryReader.ReadUInt16();
					entry.InternalAttributes = binaryReader.ReadUInt16();
					entry.ExternalAttributes = binaryReader.ReadUInt32();
					entry.LocalHeaderOffset = (long)((ulong)binaryReader.ReadUInt32());
					entry.NameBytes = binaryReader.ReadBytes(count);
					entry.CentralExtra = binaryReader.ReadBytes(count2);
					entry.Comment = binaryReader.ReadBytes(count3);
					entry.Name = Encoding.UTF8.GetString(entry.NameBytes);
					if (entry.CompressedSize == uint.MaxValue || entry.UncompressedSize == uint.MaxValue || entry.LocalHeaderOffset == (long)((ulong)uint.MaxValue))
					{
						throw new NotSupportedException("zip64 entries are not supported: " + entry.Name);
					}
					list.Add(entry);
				}
				foreach (Entry entry2 in list)
				{
					fileStream.Position = entry2.LocalHeaderOffset;
					if (binaryReader.ReadUInt32() != ApkZip.kLocalHeaderSignature)
					{
						throw new InvalidDataException("bad local header for " + entry2.Name);
					}
					fileStream.Position = entry2.LocalHeaderOffset + 26L;
					int num3 = (int)binaryReader.ReadUInt16();
					int num4 = (int)binaryReader.ReadUInt16();
					entry2.DataOffset = entry2.LocalHeaderOffset + 30L + (long)num3 + (long)num4;
				}
			}
			return list;
		}

		private static void SetStoredData(Entry entry, byte[] data)
		{
			entry.NewData = data;
			entry.Method = 0;
			entry.VersionNeeded = 10;
			entry.Flags = (ushort)(entry.Flags & ApkZip.kUtf8NameFlag);
			entry.Crc32 = ApkZip.ComputeCrc32(data);
			entry.CompressedSize = (uint)data.Length;
			entry.UncompressedSize = (uint)data.Length;
		}

		/// <summary>
		/// Padding so the entry's data starts on an alignment boundary, written as the 0xD935 "Android zip alignment"
		/// extra field that current zipalign uses (id, size, alignment, zeros), so it is at least 6 bytes.
		/// </summary>
		private static byte[] AlignmentExtra(long headerEnd, int alignment)
		{
			int num = (int)((long)alignment - headerEnd % (long)alignment) % alignment;
			if (num == 0)
			{
				return new byte[0];
			}
			while (num < 6)
			{
				num += alignment;
			}
			byte[] array = new byte[num];
			array[0] = 0x35;
			array[1] = 0xD9;
			array[2] = (byte)(num - 4);
			array[3] = (byte)(num - 4 >> 8);
			array[4] = (byte)alignment;
			array[5] = (byte)(alignment >> 8);
			return array;
		}

		private static long FindEndOfCentralDirectory(FileStream stream)
		{
			// The end record is 22 bytes plus a comment of up to 65535 bytes.
			int num = (int)Math.Min(stream.Length, 22L + 65535L);
			byte[] array = new byte[num];
			stream.Position = stream.Length - (long)num;
			stream.ReadExactly(array, 0, num);
			for (int i = num - 22; i >= 0; i--)
			{
				if (array[i] == 80 && array[i + 1] == 75 && array[i + 2] == 5 && array[i + 3] == 6)
				{
					return stream.Length - (long)num + (long)i;
				}
			}
			throw new InvalidDataException("not a zip file: " + stream.Name);
		}

		private static void SetDosTime(Entry entry, DateTime time)
		{
			entry.Time = (ushort)(time.Hour << 11 | time.Minute << 5 | time.Second / 2);
			entry.Date = (ushort)(time.Year - 1980 << 9 | time.Month << 5 | time.Day);
		}

		private static void CopyBytes(Stream from, Stream to, long count)
		{
			byte[] array = new byte[81920];
			while (count > 0L)
			{
				int num = from.Read(array, 0, (int)Math.Min((long)array.Length, count));
				if (num <= 0)
				{
					throw new EndOfStreamException();
				}
				to.Write(array, 0, num);
				count -= (long)num;
			}
		}

		private static uint ComputeCrc32(byte[] data)
		{
			if (ApkZip._CrcTable == null)
			{
				uint[] array = new uint[256];
				for (uint num = 0U; num < 256U; num += 1U)
				{
					uint num2 = num;
					for (int i = 0; i < 8; i++)
					{
						num2 = (((num2 & 1U) != 0U) ? (0xEDB88320U ^ num2 >> 1) : (num2 >> 1));
					}
					array[(int)num] = num2;
				}
				ApkZip._CrcTable = array;
			}
			uint num3 = uint.MaxValue;
			foreach (byte b in data)
			{
				num3 = (ApkZip._CrcTable[(int)((num3 ^ (uint)b) & 255U)] ^ num3 >> 8);
			}
			return ~num3;
		}

		private static ushort ToUInt16(int value)
		{
			if (value > 65535)
			{
				throw new NotSupportedException("too many zip entries for a non-zip64 archive");
			}
			return (ushort)value;
		}

		private static uint ToUInt32(long value, string what)
		{
			if (value >= (long)((ulong)uint.MaxValue))
			{
				throw new NotSupportedException(what + " needs zip64");
			}
			return (uint)value;
		}

		private static uint[] _CrcTable;

		private const uint kLocalHeaderSignature = 0x04034B50U;

		private const uint kCentralHeaderSignature = 0x02014B50U;

		private const uint kEndOfCentralDirectorySignature = 0x06054B50U;

		private const int kDataDescriptorFlag = 0x0008;

		private const int kUtf8NameFlag = 0x0800;
	}
}
