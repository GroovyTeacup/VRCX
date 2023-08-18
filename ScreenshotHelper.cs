﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace VRCX
{
    internal static class ScreenshotHelper
    {
        private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
        private static readonly byte[] pngSignatureBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly Dictionary<string, ScreenshotMetadata> metadataCache = new Dictionary<string, ScreenshotMetadata>();

        public enum ScreenshotSearchType
        {
            Username,
            UserID,
            WorldName,
            WorldID,
        }

        public class ScreenshotHelperException : Exception
        {
            public ScreenshotHelperException(string message) : base(message) { }
        }

        public static List<ScreenshotMetadata> FindScreenshots(string query, string directory, ScreenshotSearchType searchType)
        {
            var result = new List<ScreenshotMetadata>();

            var files = Directory.GetFiles(directory, "*.png", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                ScreenshotMetadata metadata = null;

                if (!metadataCache.TryGetValue(file, out metadata))
                {
                    try 
                    {
                        metadata = GetScreenshotMetadata(file);
                        metadataCache.Add(file, metadata);
                    }
                    catch (ScreenshotHelperException ex)
                    {
                        logger.Error(ex, "Failed to get metadata for file '{0}' during search", file);
                        continue;
                    }
                }

                if (metadata == null) continue;

                switch (searchType)
                {
                    case ScreenshotSearchType.Username:
                        if (metadata.ContainsPlayerName(query, true, true))
                            result.Add(metadata);

                        break;
                    case ScreenshotSearchType.UserID:
                        if (metadata.ContainsPlayerID(query))
                            result.Add(metadata);

                        break;
                    case ScreenshotSearchType.WorldName:
                        if (metadata.World.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) != -1)
                            result.Add(metadata);

                        break;
                    case ScreenshotSearchType.WorldID:
                        if (metadata.World.Id == query)
                            result.Add(metadata);

                        break;
                    
                }
            }

            logger.ConditionalDebug("Found {0}/{1} screenshots matching query '{2}' of type '{3}'", result.Count, files.Length, query, searchType);

            return result;
        }

        /// <summary>
        /// Retrieves metadata from a PNG screenshot file and attempts to parse it.
        /// </summary>
        /// <param name="path">The path to the PNG screenshot file.</param>
        /// <returns>A JObject containing the metadata or null if no metadata was found.</returns>
        public static ScreenshotMetadata GetScreenshotMetadata(string path)
        {
            // Early return if file doesn't exist, or isn't a PNG(Check both extension and file header)
            if (!File.Exists(path) || !path.EndsWith(".png") || !IsPNGFile(path))
                return null;

            if (metadataCache.TryGetValue(path, out var cachedMetadata))
                return cachedMetadata;

            string metadataString;

            // Get the metadata string from the PNG file
            try
            {
                metadataString = ReadPNGDescriptionFileStream(path);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to read PNG description for file '{0}'", path);
                throw new ScreenshotHelperException("Failed to read PNG description");
            }

            // If the metadata string is empty for some reason, there's nothing to parse.
            if (string.IsNullOrEmpty(metadataString))
                return null;

            // Check for specific metadata string start sequences
            if (metadataString.StartsWith("lfs") || metadataString.StartsWith("screenshotmanager"))
            {
                try
                {
                    var result = ScreenshotHelper.ParseLfsPicture(metadataString).ToObject<ScreenshotMetadata>();
                    result.SourceFile = path;

                    return result;
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Failed to parse LFS/ScreenshotManager metadata for file '{0}'", path);
                    throw new ScreenshotHelperException("Failed to parse LFS/ScreenshotManager metadata");
                }   
            }

            // If not JSON metadata, return early so we're not throwing/catching pointless exceptions
            if (!metadataString.StartsWith("{"))
            {
                logger.ConditionalDebug("Screenshot file '{0}' has unknown non-JSON metadata:\n{1}\n", path, metadataString);
                throw new ScreenshotHelperException("Unknown non-JSON metadata");
            }

            // Parse the metadata as VRCX JSON metadata
            try
            {
                var result = JsonConvert.DeserializeObject<ScreenshotMetadata>(metadataString);
                result.SourceFile = path;

                return result;
            }
            catch (JsonException ex)
            {
                logger.Error(ex, "Failed to parse screenshot metadata JSON for file '{0}'", path);
                throw new ScreenshotHelperException("Failed to parse screenshot metadata JSON");
            }
        }

        /// <summary>
        ///     Writes a text description into a PNG file at the specified path.
        ///     Creates an iTXt PNG chunk in the target file, using the Description tag, with the specified text.
        /// </summary>
        /// <param name="path">The file path of the PNG file in which the description is to be written.</param>
        /// <param name="text">The text description that is to be written into the PNG file.</param>
        /// <returns>
        ///     <c>true</c> if the text description is successfully written to the PNG file;
        ///     otherwise, <c>false</c>.
        /// </returns>
        public static bool WritePNGDescription(string path, string text)
        {
            if (!File.Exists(path) || !IsPNGFile(path)) return false;

            var png = File.ReadAllBytes(path);

            var newChunkIndex = FindEndOfChunk(png, "IHDR");
            if (newChunkIndex == -1) return false;

            // If this file already has a text chunk, chances are it got logged twice for some reason. Stop.
            var existingiTXt = FindChunkIndex(png, "iTXt");
            if (existingiTXt != -1) return false;

            var newChunk = new PNGChunk("iTXt");
            newChunk.InitializeTextChunk("Description", text);

            var newFile = png.ToList();
            newFile.InsertRange(newChunkIndex, newChunk.ConstructChunkByteArray());

            File.WriteAllBytes(path, newFile.ToArray());

            return true;
        }

        /// <summary>
        ///     Reads a text description from a PNG file at the specified path.
        ///     Reads any existing iTXt PNG chunk in the target file, using the Description tag.
        /// </summary>
        /// <param name="path">The file path of the PNG file in which the description is to be read from.</param>
        /// <returns>
        ///     The text description that is read from the PNG file.
        /// </returns>
        public static string ReadPNGDescription(string path)
        {
            if (!File.Exists(path) || !IsPNGFile(path)) return null;

            var png = File.ReadAllBytes(path);
            var existingiTXt = FindChunk(png, "iTXt");
            if (existingiTXt == null) return null;

            return existingiTXt.GetText("Description");
        }

        /// <summary>
        ///     Reads a text description from a PNG file at the specified path.
        ///     Reads any existing iTXt PNG chunk in the target file, using the Description tag.
        /// </summary>
        /// <param name="path">The file path of the PNG file in which the description is to be read from.</param>
        /// <returns>
        ///     The text description that is read from the PNG file.
        /// </returns>
        public static string ReadPNGDescriptionFileStream(string path)
        {
            if (!File.Exists(path) || !IsPNGFile(path)) return null;

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 512))
            {
                var existingiTXt = FindChunk(stream, "iTXt");
                if (existingiTXt == null) return null;

                return existingiTXt.GetText("Description");
            }
        }

        public static string ReadPNGResolution(string path)
        {
            if (!File.Exists(path) || !IsPNGFile(path)) return null;

            var png = File.ReadAllBytes(path);
            var existingpHYs = FindChunk(png, "IHDR");
            if (existingpHYs == null) return null;

            var text = existingpHYs.GetResolution();

            return text;
        }

        /// <summary>
        ///     Determines whether the specified file is a PNG file. We do this by checking if the first 8 bytes in the file path match the PNG signature.
        /// </summary>
        /// <param name="path">The path of the file to check.</param>
        /// <returns></returns>
        public static bool IsPNGFile(string path)
        {
            // Read only the first 8 bytes of the file to check if it's a PNG file instead of reading the entire thing into memory just to see check a couple bytes.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length < 33) return false; // I don't remember how I came up with this number, but a PNG file below this size is not going to be valid for our purposes.

                var signature = new byte[8];
                fs.Read(signature, 0, 8);
                return signature.SequenceEqual(pngSignatureBytes);
            }
        }

        /// <summary>
        ///     Finds the index of the first byte of the specified chunk type in the specified PNG file.
        /// </summary>
        /// <param name="png">Array of bytes representing a PNG file.</param>
        /// <param name="type">Type of PMG chunk to find</param>
        /// <returns></returns>
        private static int FindChunkIndex(byte[] png, string type)
        {
            // The first 8 bytes of the file are the png signature, so we can skip them.
            var index = 8;

            while (index < png.Length)
            {
                var chunkLength = new byte[4];
                Array.Copy(png, index, chunkLength, 0, 4);

                // BitConverter wants little endian(unless your system is big endian for some reason), PNG multi-byte integers are big endian. So we reverse the array.
                if (BitConverter.IsLittleEndian) Array.Reverse(chunkLength);

                var length = BitConverter.ToInt32(chunkLength, 0);

                // We don't need to reverse strings since UTF-8 strings aren't affected by endianess, given that they're a sequence of bytes. 
                var chunkName = new byte[4];
                Array.Copy(png, index + 4, chunkName, 0, 4);
                var name = Encoding.UTF8.GetString(chunkName);

                if (name == type)
                {
                    return index;
                }

                if (name == "IEND") // Nothing should exist past IEND in a normal png file, so we should stop parsing here to avoid trying to parse junk data.
                {
                    return -1;
                }

                // The chunk length is 4 bytes, the chunk name is 4 bytes, the chunk data is length bytes, and the chunk CRC is 4 bytes.
                // We add 12 to the index to get to the start of the next chunk in the file on the next loop.
                index += length + 12;
            }

            return -1;
        }

        /// <summary>
        ///     Finds the index of the first byte of the specified chunk type in the specified PNG file.
        /// </summary>
        /// <param name="fs">FileStream of a PNG file.</param>
        /// <param name="type">Type of PMG chunk to find</param>
        /// <returns></returns>
        private static int FindChunkIndex(FileStream fs, string type)
        {
            // The first 8 bytes of the file are the png signature, so we can skip them.
            var index = 8;

            byte[] buffer = new byte[128 * 1024];
            fs.Position = 0;
            fs.Read(buffer, 0, buffer.Length);

            while (index + 12 < buffer.Length)
            {
                var chunkLength = new byte[4];
                Array.Copy(buffer, index, chunkLength, 0, 4);

                // BitConverter wants little endian(unless your system is big endian for some reason), PNG multi-byte integers are big endian. So we reverse the array.
                if (BitConverter.IsLittleEndian) Array.Reverse(chunkLength);

                var length = BitConverter.ToInt32(chunkLength, 0);

                // We don't need to reverse strings since UTF-8 strings aren't affected by endianess, given that they're a sequence of bytes. 
                var chunkName = new byte[4];
                Array.Copy(buffer, index + 4, chunkName, 0, 4);
                var name = Encoding.UTF8.GetString(chunkName);

                if (name == type)
                {
                    return index;
                }

                if (name == "IEND") // Nothing should exist past IEND in a normal png file, so we should stop parsing here to avoid trying to parse junk data.
                {
                    return -1;
                }

                // The chunk length is 4 bytes, the chunk name is 4 bytes, the chunk data is length bytes, and the chunk CRC is 4 bytes.
                index += length + 12;
            }

            return -1;
        }

        /// <summary>
        ///     Finds the index of the end of the specified chunk type in the specified PNG file.
        /// </summary>
        /// <param name="png">Array of bytes representing a PNG file.</param>
        /// <param name="type">Type of PMG chunk to find</param>
        /// <returns></returns>
        private static int FindEndOfChunk(byte[] png, string type)
        {
            var index = FindChunkIndex(png, type);
            if (index == -1) return index;

            var chunkLength = new byte[4];
            Array.Copy(png, index, chunkLength, 0, 4);
            Array.Reverse(chunkLength);
            var length = BitConverter.ToInt32(chunkLength, 0);

            return index + length + 12;
        }

        /// <summary>
        ///     Finds the specified chunk type in the specified PNG file and returns it as a PNGChunk.
        /// </summary>
        /// <param name="png">Array of bytes representing a PNG file</param>
        /// <param name="type">Type of PMG chunk to find</param>
        /// <returns>PNGChunk</returns>
        private static PNGChunk FindChunk(byte[] png, string type)
        {
            var index = FindChunkIndex(png, type);
            if (index == -1) return null;

            var chunkLength = new byte[4];
            Array.Copy(png, index, chunkLength, 0, 4);
            Array.Reverse(chunkLength);
            var length = BitConverter.ToInt32(chunkLength, 0);

            var chunkData = new byte[length];
            Array.Copy(png, index + 8, chunkData, 0, length);

            return new PNGChunk(type, chunkData);
        }

        /// <summary>
        ///     Finds the specified chunk type in the specified PNG file and returns it as a PNGChunk.
        /// </summary>
        /// <param name="fs">FileStream of a PNG file.</param>
        /// <param name="type">Type of PMG chunk to find</param>
        /// <returns>PNGChunk</returns>
        private static PNGChunk FindChunk(FileStream fs, string type)
        {
            var index = FindChunkIndex(fs, type);
            if (index == -1) return null;

            var chunkLength = new byte[4];
            fs.Position = index;
            fs.Read(chunkLength, 0, 4);
            Array.Reverse(chunkLength);
            var length = BitConverter.ToInt32(chunkLength, 0);

            var chunkData = new byte[length];
            fs.Position = index + 8;
            fs.Read(chunkData, 0, length);

            return new PNGChunk(type, chunkData);
        }

        /// <summary>
        /// Parses the metadata string of a vrchat screenshot with taken with LFS and returns a JObject containing the parsed data.
        /// </summary>
        /// <param name="metadataString">The metadata string to parse.</param>
        /// <returns>A JObject containing the parsed data.</returns>
        public static JObject ParseLfsPicture(string metadataString)
        {
            var metadata = new JObject();
            // lfs|2|author:usr_032383a7-748c-4fb2-94e4-bcb928e5de6b,Natsumi-sama|world:wrld_b016712b-5ce6-4bcb-9144-c8ed089b520f,35372,pet park test|pos:-60.49379,-0.002925932,5.805772|players:usr_9d73bff9-4543-4b6f-a004-9e257869ff50,-0.85,-0.17,-0.58,Olivia.;usr_3097f91e-a816-4c7a-a625-38fbfdee9f96,12.30,13.72,0.08,Zettai Ryouiki;usr_032383a7-748c-4fb2-94e4-bcb928e5de6b,0.68,0.32,-0.28,Natsumi-sama;usr_7525f45f-517e-442b-9abc-fbcfedb29f84,0.51,0.64,0.70,Weyoun
            // lfs|2|author:usr_8c0a2f22-26d4-4dc9-8396-2ab40e3d07fc,knah|world:wrld_fb4edc80-6c48-43f2-9bd1-2fa9f1345621,35341,Luminescent Ledge|pos:8.231676,0.257298,-0.1983307|rq:2|players:usr_65b9eeeb-7c91-4ad2-8ce4-addb1c161cd6,0.74,0.59,1.57,Jakkuba;usr_6a50647f-d971-4281-90c3-3fe8caf2ba80,8.07,9.76,0.16,SopwithPup;usr_8c0a2f22-26d4-4dc9-8396-2ab40e3d07fc,0.26,1.03,-0.28,knah;usr_7f593ad1-3e9e-4449-a623-5c1c0a8d8a78,0.15,0.60,1.46,NekOwneD
            // lfs|cvr|1|author:047b30bd-089d-887c-8734-b0032df5d176,Hordini|world:2e73b387-c6d4-45e9-b998-0fd6aa122c1d,i+efec20004ef1cd8b-404003-93833f-1aee112a,Bono's Basement (Anime) (#816724)|pos:2.196716,0.01250899,-3.817466|players:5301af21-eb8d-7b36-3ef4-b623fa51c2c6,3.778407,0.01250887,-3.815876,DDAkebono;f9e5c36c-41b0-7031-1185-35b4034010c0,4.828233,0.01250893,-3.920135,Natsumi
            var lfsParts = metadataString.Split('|');
            if (lfsParts[1] == "cvr")
                lfsParts = lfsParts.Skip(1).ToArray();

            var version = int.Parse(lfsParts[1]);
            var application = lfsParts[0];
            metadata.Add("application", application);
            metadata.Add("version", version);

            if (application == "screenshotmanager")
            {
                // screenshotmanager|0|author:usr_290c03d6-66cc-4f0e-b782-c07f5cfa8deb,VirtualTeacup|wrld_6caf5200-70e1-46c2-b043-e3c4abe69e0f,47213,The Great Pug
                var author = lfsParts[2].Split(',');
                metadata.Add("author", new JObject
                {
                    { "id", author[0] },
                    { "displayName", author[1] }
                });
                var world = lfsParts[3].Split(',');
                metadata.Add("world", new JObject
                {
                    { "id", world[0] },
                    { "name", world[2] },
                    { "instanceId", world[1] }
                });
                return metadata;
            }

            for (var i = 2; i < lfsParts.Length; i++)
            {
                var split = lfsParts[i].Split(':');
                var key = split[0];
                var value = split[1];

                if (String.IsNullOrEmpty(value)) // One of my LFS files had an empty value for 'players:'. not pog
                    continue;

                var parts = value.Split(',');

                switch (key)
                {
                    case "author":
                        metadata.Add("author", new JObject
                        {
                            { "id", application == "cvr" ? string.Empty : parts[0] },
                            { "displayName", application == "cvr" ? $"{parts[1]} ({parts[0]})" : parts[1] }
                        });
                        break;

                    case "world":
                        metadata.Add("world", new JObject
                        {
                            { "id", application == "cvr" || version == 1 ? string.Empty : parts[0] },
                            { "name", application == "cvr" ? $"{parts[2]} ({parts[0]})" : (version == 1 ? value : parts[2]) },
                            { "instanceId", application == "cvr" || version == 1 ? string.Empty : parts[1] }
                        });
                        break;

                    case "pos":
                        metadata.Add("pos", new JObject
                        {
                            { "x", parts[0] },
                            { "y", parts[1] },
                            { "z", parts[2] }
                        });
                        break;

                    case "rq":
                        metadata.Add("rq", value);
                        break;

                    case "players":
                        var playersArray = new JArray();
                        var players = value.Split(';');

                        foreach (var player in players)
                        {
                            var playerParts = player.Split(',');
                            playersArray.Add(new JObject
                            {
                                { "id", application == "cvr" ? string.Empty : playerParts[0] },
                                { "x", playerParts[1] },
                                { "y", playerParts[2] },
                                { "z", playerParts[3] },
                                { "displayName", application == "cvr" ? $"{playerParts[4]} ({playerParts[0]})" : playerParts[4] }
                            });
                        }
                        metadata.Add("players", playersArray);
                        break;
                }
            }

            return metadata;
        }
    }

    // See http://www.libpng.org/pub/png/spec/1.2/PNG-Chunks.html 4.2.3
    // Basic PNG Chunk Structure: Length(int, 4 bytes) | Type (string, 4 bytes) | chunk data (Depends on type) | 32-bit CRC code (4 bytes)
    // basic tEXt data structure: Keyword (1-79 bytes string) | Null separator (1 byte) | Text (x bytes)
    // basic iTXt data structure: Keyword (1-79 bytes string) | Null separator (1 byte) | Compression flag (1 byte) | Compression method (1 byte) | Language tag (0-x bytes) | Null separator | Translated keyword (0-x bytes) | Null separator | Text (x bytes)

    // Proper practice here for arbitrary image processing would be to check the PNG file being passed for any existing iTXt chunks with the same keyword that we're trying to use; If we find one, we replace that chunk's data instead of creating a new chunk.
    // Luckily, VRChat should never do this! Bugs notwithstanding, we should never re-process a png file either. So we're just going to skip that logic.
    // This code would be HORRIBLE for general parsing of PNG files/metadata. It's not really meant to do that, it's just meant to do exactly what we need it to do.
    internal class PNGChunk
    {
        // crc lookup table
        private static uint[] crcTable;

        // init lookup table and store crc for iTXt
        private static readonly uint iTXtCrc = Crc32(new[] { (byte)'i', (byte)'T', (byte)'X', (byte)'t' }, 0, 4, 0);
        private readonly Encoding keywordEncoding = Encoding.GetEncoding("ISO-8859-1"); // ISO-8859-1/Latin1 is the encoding used for the keyword in text chunks. 
        public List<byte> ChunkDataBytes;
        public int ChunkDataLength;
        public string ChunkType;

        public PNGChunk(string chunkType)
        {
            ChunkType = chunkType;
            ChunkDataBytes = new List<byte>();
        }

        public PNGChunk(string chunkType, byte[] bytes)
        {
            ChunkType = chunkType;
            ChunkDataBytes = bytes.ToList();
            ChunkDataLength = bytes.Length;
        }

        /// <summary>
        ///     Initializes this PNGChunk's data in the format of an iTXt chunk with the specified keyword and text.
        /// </summary>
        /// <param name="keyword">Keyword for text chunk</param>
        /// <param name="text">Text data for text chunk</param>
        public void InitializeTextChunk(string keyword, string text)
        {
            // Create our chunk data byte array
            ChunkDataBytes.AddRange(keywordEncoding.GetBytes(keyword)); // keyword
            ChunkDataBytes.Add(0x0); // Null separator
            ChunkDataBytes.Add(0x0); // Compression flag
            ChunkDataBytes.Add(0x0); // Compression method
            ChunkDataBytes.Add(0x0); // Null separator (skipping over language tag byte)
            ChunkDataBytes.Add(0x0); // Null separator (skipping over translated keyword byte)
            ChunkDataBytes.AddRange(Encoding.UTF8.GetBytes(text)); // our text

            ChunkDataLength = ChunkDataBytes.Count;
        }

        /// <summary>
        ///     Constructs and returns a full, coherent PNG chunk from this PNGChunk's data.
        /// </summary>
        /// <returns>PNG chunk byte array</returns>
        public byte[] ConstructChunkByteArray()
        {
            var chunk = new List<byte>();

            var chunkLengthBytes = BitConverter.GetBytes(ChunkDataLength);
            var chunkCRCBytes = BitConverter.GetBytes(Crc32(ChunkDataBytes.ToArray(), 0, ChunkDataLength, iTXtCrc));

            // Reverse the chunk length bytes/CRC bytes if system is little endian since PNG integers are big endian
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(chunkLengthBytes);
                Array.Reverse(chunkCRCBytes);
            }

            chunk.AddRange(chunkLengthBytes); // add data length
            chunk.AddRange(Encoding.UTF8.GetBytes(ChunkType)); // add chunk type
            chunk.AddRange(ChunkDataBytes); // Add chunk data
            chunk.AddRange(chunkCRCBytes); // Add chunk CRC32 hash. 

            return chunk.ToArray();
        }

        /// <summary>
        ///     Gets the text from an iTXt chunk
        /// </summary>
        /// <param name="keyword">Keyword of the text chunk</param>
        /// <returns>Text from chunk.</returns>
        public string GetText(string keyword)
        {
            var offset = keywordEncoding.GetByteCount(keyword) + 5;
            return Encoding.UTF8.GetString(ChunkDataBytes.ToArray(), offset, ChunkDataBytes.Count - offset);
        }

        public string GetResolution()
        {
            var x = BitConverter.ToInt32(ChunkDataBytes.Take(4).Reverse().ToArray(), 0);
            var y = BitConverter.ToInt32(ChunkDataBytes.Skip(4).Take(4).Reverse().ToArray(), 0);
            return $"{x}x{y}";
        }

        // Crc32 implementation from
        // https://web.archive.org/web/20150825201508/http://upokecenter.dreamhosters.com/articles/png-image-encoder-in-c/
        private static uint Crc32(byte[] stream, int offset, int length, uint crc)
        {
            uint c;
            if (crcTable == null)
            {
                crcTable = new uint[256];
                for (uint n = 0; n <= 255; n++)
                {
                    c = n;
                    for (var k = 0; k <= 7; k++)
                    {
                        if ((c & 1) == 1)
                            c = 0xEDB88320 ^ ((c >> 1) & 0x7FFFFFFF);
                        else
                            c = (c >> 1) & 0x7FFFFFFF;
                    }

                    crcTable[n] = c;
                }
            }

            c = crc ^ 0xffffffff;
            var endOffset = offset + length;
            for (var i = offset; i < endOffset; i++)
            {
                c = crcTable[(c ^ stream[i]) & 255] ^ ((c >> 8) & 0xFFFFFF);
            }

            return c ^ 0xffffffff;
        }
    }
}