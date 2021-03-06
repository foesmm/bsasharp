﻿namespace BSAsharp
{
    public sealed class ArchiveSettings
    {
        public bool DefaultCompressed { get; internal set; }
        public bool BStringPrefixed { get; internal set; }
        public CompressionOptions Options { get; internal set; }

        public ArchiveSettings(bool defaultCompressed, bool bStringPrefixed, CompressionOptions options)
        {
            DefaultCompressed = defaultCompressed;
            BStringPrefixed = bStringPrefixed;
            Options = options;
        }
        internal ArchiveSettings()
        {
        }
    }
}
