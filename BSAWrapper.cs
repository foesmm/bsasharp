﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using BSAsharp.Format;
using BSAsharp.Extensions;
using System.Threading;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;

namespace BSAsharp
{
    public class BSAWrapper : SortedSet<BSAFolder>, IDisposable
    {
        public const int
            FALLOUT_VERSION = 0x68,
            HEADER_OFFSET = 0x24, //sizeof(BSAHeader)
            SIZE_RECORD = 0x10, //sizeof(FolderRecord) or sizeof(FileRecord)
            SIZE_RECORD_OFFSET = 0xC; //SIZE_RECORD - sizeof(uint)

        static readonly char[] BSA_GREET = "BSA\0".ToCharArray();

        private readonly BSAHeader _readHeader;

        private Dictionary<BSAFolder, uint> _folderRecordOffsetsA;
        private Dictionary<BSAFolder, uint> _folderRecordOffsetsB;

        private Dictionary<BSAFile, uint> _fileRecordOffsetsA;
        private Dictionary<BSAFile, uint> _fileRecordOffsetsB;

        public ArchiveSettings Settings { get; private set; }

        /// <summary>
        /// Creates a new BSAWrapper instance around an existing BSA file
        /// </summary>
        /// <param name="bsaPath">The path of the file to open</param>
        public BSAWrapper(string bsaPath)
            : this(MemoryMappedFile.CreateFromFile(bsaPath))
        {
        }
        /// <summary>
        /// Creates a new BSAWrapper instance from an existing folder structure
        /// </summary>
        /// <param name="packFolder">The path of the folder to pack</param>
        /// <param name="defaultCompressed">The default compression state for the archive</param>
        public BSAWrapper(string packFolder, ArchiveSettings settings)
            : this(settings)
        {
            Pack(packFolder);
        }
        /// <summary>
        /// Creates an empty BSAWrapper instance that can be modified and saved to a BSA file
        /// </summary>
        public BSAWrapper(ArchiveSettings settings)
            : this(new SortedSet<BSAFolder>())
        {
            this.Settings = settings;
        }

        //wtf C#
        //please get real ctor overloads someday
        private BSAWrapper(MemoryMappedFile BSAMap)
            : this(new MemoryMappedBSAReader(BSAMap))
        {
            this._bsaMap = BSAMap;
        }
        private BSAWrapper(MemoryMappedBSAReader BSAReader)
            : this(BSAReader.Read())
        {
            this._readHeader = BSAReader.Header;
            this.Settings = BSAReader.Settings;

            this._bsaReader = BSAReader;
        }
        private BSAWrapper(IEnumerable<BSAFolder> collection)
            : base(collection, HashComparer.Instance)
        {
        }
        ~BSAWrapper()
        {
            Dispose(false);
        }

        private readonly MemoryMappedBSAReader _bsaReader;
        private readonly MemoryMappedFile _bsaMap;

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_bsaReader != null)
                    _bsaReader.Dispose();
                if (_bsaMap != null)
                    _bsaMap.Dispose();
            }
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public void Pack(string packFolder)
        {
            var packDirectories = Directory.EnumerateDirectories(packFolder, "*", SearchOption.AllDirectories);
            var bsaFolders = packDirectories
                .Select(path =>
                {
                    var packFiles = Directory.EnumerateFiles(path);

                    var trimmedPath = path.Replace(packFolder, "").TrimStart(Path.DirectorySeparatorChar);
                    var bsaFiles = from file in packFiles
                                   let fileName = Path.GetFileName(file)
                                   let fnNoExt = Path.GetFileNameWithoutExtension(fileName)
                                   where !string.IsNullOrEmpty(fnNoExt)
                                   select new BSAFile(trimmedPath, fileName, Settings, File.ReadAllBytes(file), false);

                    return new BSAFolder(trimmedPath, bsaFiles);
                });

            bsaFolders.ToList()
                .ForEach(folder => Add(folder));
        }

        public void Extract(string outFolder)
        {
            if (Directory.Exists(outFolder))
                Directory.Delete(outFolder, true);

            foreach (var folder in this)
            {
                Directory.CreateDirectory(Path.Combine(outFolder, folder.Path));

                foreach (var file in folder)
                {
                    var filePath = Path.Combine(outFolder, file.Filename);
                    File.WriteAllBytes(filePath, file.GetSaveData(true));
                }
            }
        }

        public void Save(string outBsa, bool recreate = false)
        {
            bool reuseAttributes = _readHeader != null && !recreate;

            _folderRecordOffsetsA = new Dictionary<BSAFolder, uint>();
            _folderRecordOffsetsB = new Dictionary<BSAFolder, uint>();

            _fileRecordOffsetsA = new Dictionary<BSAFile, uint>();
            _fileRecordOffsetsB = new Dictionary<BSAFile, uint>();

            var allFiles = this.SelectMany(fold => fold).ToList();
            var allFileNames = allFiles.Select(file => file.Name).ToList();

            File.Delete(outBsa);
            using (var writer = new BinaryWriter(File.OpenWrite(outBsa)))
            {
                var archFlags = reuseAttributes ? _readHeader.archiveFlags :
                    ArchiveFlags.NamedDirectories | ArchiveFlags.NamedFiles
                    | (Settings.DefaultCompressed ? ArchiveFlags.Compressed : 0)
                    | (Settings.BStringPrefixed ? ArchiveFlags.BStringPrefixed : 0);

                var header = new BSAHeader
                {
                    field = BSA_GREET,
                    version = FALLOUT_VERSION,
                    offset = HEADER_OFFSET,
                    archiveFlags = archFlags,
                    folderCount = (uint)this.Count(),
                    fileCount = (uint)allFileNames.Count,
                    totalFolderNameLength = (uint)this.Sum(bsafolder => bsafolder.Path.Length + 1),
                    totalFileNameLength = (uint)allFileNames.Sum(file => file.Length + 1),
                    fileFlags = reuseAttributes ? _readHeader.fileFlags : CreateFileFlags(allFileNames)
                };

                writer.WriteStruct<BSAHeader>(header);

                var orderedFolders =
                    (from folder in this //presorted
                     let record = CreateFolderRecord(folder)
                     //orderby record.hash
                     select new { folder, record }).ToList();

                orderedFolders.ForEach(a => WriteFolderRecord(writer, a.folder, a.record));

                orderedFolders.ForEach(a => WriteFileRecordBlock(writer, a.folder, header.totalFileNameLength));

                allFileNames.ForEach(fileName => writer.WriteCString(fileName));

                allFiles.ForEach(file =>
                {
                    _fileRecordOffsetsB.Add(file, (uint)writer.BaseStream.Position);
                    writer.Write(file.GetSaveData(false));
                });

                var folderRecordOffsets = _folderRecordOffsetsA.Zip(_folderRecordOffsetsB, (kvpA, kvpB) => new KeyValuePair<uint, uint>(kvpA.Value, kvpB.Value));
                var fileRecordOffsets = _fileRecordOffsetsA.Zip(_fileRecordOffsetsB, (kvpA, kvpB) => new KeyValuePair<uint, uint>(kvpA.Value, kvpB.Value));
                var completeOffsets = folderRecordOffsets.Concat(fileRecordOffsets);

                completeOffsets.ToList()
                    .ForEach(kvp =>
                    {
                        writer.BaseStream.Seek(kvp.Key, SeekOrigin.Begin);
                        writer.Write(kvp.Value);
                    });
            }
        }

        private FolderRecord CreateFolderRecord(BSAFolder folder)
        {
            return new FolderRecord
            {
                hash = folder.Hash,
                count = (uint)folder.Count(),
                offset = 0
            };
        }

        private void WriteFolderRecord(BinaryWriter writer, BSAFolder folder, FolderRecord rec)
        {
            _folderRecordOffsetsA.Add(folder, (uint)writer.BaseStream.Position + SIZE_RECORD_OFFSET);
            writer.WriteStruct(rec);
        }

        private FileRecord CreateFileRecord(BSAFile file)
        {
            return new FileRecord
            {
                hash = file.Hash,
                size = file.CalculateRecordSize(),
                offset = 0
            };
        }

        private void WriteFileRecordBlock(BinaryWriter writer, BSAFolder folder, uint totalFileNameLength)
        {
            _folderRecordOffsetsB.Add(folder, (uint)writer.BaseStream.Position + totalFileNameLength);
            writer.WriteBZString(folder.Path);

            foreach (var file in folder)
            {
                var record = CreateFileRecord(file);

                _fileRecordOffsetsA.Add(file, (uint)writer.BaseStream.Position + SIZE_RECORD_OFFSET);
                writer.WriteStruct(record);
            }
        }

        private FileFlags CreateFileFlags(IEnumerable<string> allFiles)
        {
            FileFlags flags = 0;

            //flatten children in folders, take extension of each bsafile name and convert to uppercase, take distinct
            var extSet = new HashSet<string>(allFiles.Select(filename => Path.GetExtension(filename).ToUpperInvariant()).Distinct());

            //if this gets unwieldy, could foreach it and have a fall-through switch
            if (extSet.Contains(".NIF"))
                flags |= FileFlags.Nif;
            if (extSet.Contains(".DDS"))
                flags |= FileFlags.Dds;
            if (extSet.Contains(".XML"))
                flags |= FileFlags.Xml;
            if (extSet.Contains(".WAV"))
                flags |= FileFlags.Wav;
            if (extSet.Contains(".MP3") || extSet.Contains(".OGG"))
                flags |= FileFlags.Ogg;
            if (extSet.Contains(".TXT") || extSet.Contains(".HTML") || extSet.Contains(".BAT") || extSet.Contains(".SCC"))
                flags |= FileFlags.Txt;
            if (extSet.Contains(".SPT"))
                flags |= FileFlags.Spt;
            if (extSet.Contains(".TEX") || extSet.Contains(".FNT"))
                flags |= FileFlags.Tex;
            if (extSet.Contains(".CTL") || extSet.Contains(".DLODSETTINGS")) //https://github.com/Ethatron/bsaopt/issues/13
                flags |= FileFlags.Ctl;

            return flags;
        }
    }
}