using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;

namespace AppAtlas.Sdk.Crash
{
    /// <summary>
    /// The debug id of a managed module: the GUID and age of the CodeView
    /// record in its PE debug directory, which is also the id of its portable
    /// PDB — what the server keys an uploaded PDB by. Read from the file on
    /// disk once per module, by hand: System.Reflection.Metadata is not in
    /// netstandard2.0, and a dependency was the thing to avoid. A single-file
    /// or in-memory module has no file and gets no id (docs/sdk-cautions.md §3).
    /// </summary>
    internal static class DebugId
    {
        private static readonly ConcurrentDictionary<Module, string> Cache = new ConcurrentDictionary<Module, string>();

        internal static string Of(Module module)
        {
            if (module == null) return null;

            return Cache.GetOrAdd(module, Read);
        }

        private static string Read(Module module)
        {
            try
            {
                var path = module.FullyQualifiedName;

                if (string.IsNullOrEmpty(path) || path == "<Unknown>" || !File.Exists(path)) return null;

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    return ReadCodeView(reader);
                }
            }
            catch (Exception)
            {
                // Not a PE we can read (Mono, IL2CPP, an odd host): no id.
                return null;
            }
        }

        /// <summary>The PE walk: DOS header, COFF header, optional header,
        /// the debug directory, and its CodeView (RSDS) entry.</summary>
        internal static string ReadCodeView(BinaryReader reader)
        {
            var stream = reader.BaseStream;

            stream.Position = 0x3C;
            var peOffset = reader.ReadUInt32();
            stream.Position = peOffset;

            if (reader.ReadUInt32() != 0x00004550) return null; // "PE\0\0"

            stream.Position += 2; // Machine
            var sectionCount = reader.ReadUInt16();
            stream.Position += 12; // TimeDateStamp, PointerToSymbolTable, NumberOfSymbols
            var optionalSize = reader.ReadUInt16();
            stream.Position += 2; // Characteristics

            var optionalStart = stream.Position;
            var magic = reader.ReadUInt16();
            var directoriesAt = optionalStart + (magic == 0x20b ? 112 : 96);

            // Entry 6 of the data directories is the debug directory.
            stream.Position = directoriesAt + 6 * 8;
            var debugRva = reader.ReadUInt32();
            var debugSize = reader.ReadUInt32();

            if (debugRva == 0 || debugSize < 28) return null;

            // Sections, for turning an RVA into a file offset.
            stream.Position = optionalStart + optionalSize;
            var sections = new uint[sectionCount, 3];

            for (var i = 0; i < sectionCount; i++)
            {
                stream.Position += 8; // Name
                sections[i, 0] = reader.ReadUInt32(); // VirtualSize
                sections[i, 1] = reader.ReadUInt32(); // VirtualAddress
                stream.Position += 4; // SizeOfRawData
                sections[i, 2] = reader.ReadUInt32(); // PointerToRawData
                stream.Position += 16;
            }

            var debugOffset = FileOffset(sections, sectionCount, debugRva);

            if (debugOffset < 0) return null;

            for (var entry = 0; entry < debugSize / 28; entry++)
            {
                stream.Position = debugOffset + entry * 28;
                stream.Position += 12; // Characteristics, TimeDateStamp, Major/Minor
                var type = reader.ReadUInt32();
                var size = reader.ReadUInt32();
                stream.Position += 4; // AddressOfRawData
                var pointer = reader.ReadUInt32();

                if (type != 2 || size < 24 || pointer == 0) continue; // IMAGE_DEBUG_TYPE_CODEVIEW

                stream.Position = pointer;

                if (reader.ReadUInt32() != 0x53445352) continue; // "RSDS"

                var guid = new Guid(reader.ReadBytes(16));
                var age = reader.ReadUInt32();

                return Format(guid, age);
            }

            return null;
        }

        /// <summary>The same spelling for a PE read here and a minidump's
        /// module record: the GUID, a dash, the age in lowercase hex.</summary>
        internal static string Format(Guid guid, uint age)
        {
            return guid.ToString("D").ToLowerInvariant() + "-" + age.ToString("x");
        }

        private static long FileOffset(uint[,] sections, int count, uint rva)
        {
            for (var i = 0; i < count; i++)
            {
                if (rva >= sections[i, 1] && rva < sections[i, 1] + Math.Max(sections[i, 0], 1u))
                {
                    return rva - sections[i, 1] + sections[i, 2];
                }
            }

            return -1;
        }
    }
}
