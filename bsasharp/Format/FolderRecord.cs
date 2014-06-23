﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace BSAsharp.Format
{
    [StructLayout(LayoutKind.Sequential)]
    public class FolderRecord //0x10
    {
        public ulong hash;
        public uint count;
        public uint offset;
    }
}