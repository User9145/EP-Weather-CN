// Dumps or patches Win32 string-table entries inside a DLL's resources.
//   dump:   GuiStrings.exe <dll> dump <needle>
//   patch:  GuiStrings.exe <dll> patch <old> <new>   (rebuilds the string block)
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

class GuiStrings
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);
    [DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr hModule);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumResourceNamesW(IntPtr hModule, IntPtr lpszType, EnumResNameDelegate lpEnumFunc, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindResourceW(IntPtr hModule, IntPtr lpName, IntPtr lpType);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindResourceExW(IntPtr hModule, IntPtr lpType, IntPtr lpName, ushort wLanguage);
    [DllImport("kernel32.dll")]
    static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);
    [DllImport("kernel32.dll")]
    static extern IntPtr LockResource(IntPtr hResData);
    [DllImport("kernel32.dll")]
    static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr BeginUpdateResourceW(string pFileName, bool bDeleteExistingResources);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool UpdateResourceW(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, byte[] lpData, uint cbData);
    [DllImport("kernel32.dll")]
    static extern bool EndUpdateResource(IntPtr hUpdate, bool fDiscard);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int LoadStringW(IntPtr hInstance, uint uID, StringBuilder lpBuffer, int nBufferMax);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool EnumResourceLanguagesW(IntPtr hModule, IntPtr lpType, IntPtr lpName, EnumResLangDelegate lpEnumFunc, IntPtr lParam);

    delegate bool EnumResNameDelegate(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, IntPtr lParam);
    delegate bool EnumResLangDelegate(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, ushort wIDLanguage, IntPtr lParam);

    const uint LOAD_LIBRARY_AS_DATAFILE = 0x2;
    static IntPtr RT_STRING = new IntPtr(6);

    static string[] ParseBlock(byte[] data)
    {
        var strs = new string[16];
        int pos = 0;
        for (int i = 0; i < 16 && pos + 2 <= data.Length; i++)
        {
            int len = BitConverter.ToUInt16(data, pos); pos += 2;
            if (len > 0 && pos + len * 2 <= data.Length)
                strs[i] = Encoding.Unicode.GetString(data, pos, len * 2);
            else strs[i] = "";
            pos += len * 2;
        }
        return strs;
    }

    static byte[] BuildBlock(string[] strs)
    {
        var ms = new MemoryStream();
        foreach (var s in strs)
        {
            ms.Write(BitConverter.GetBytes((ushort)s.Length), 0, 2);
            var b = Encoding.Unicode.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }
        return ms.ToArray();
    }

    static int Main(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("usage"); return 2; }
        string dll = args[0]; string mode = args[1]; string needle = args[2];
        string repl = args.Length > 3 ? args[3] : null;

        var h = LoadLibraryEx(dll, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
        if (h == IntPtr.Zero) { Console.WriteLine("load failed"); return 1; }

        var blocks = new List<long>();
        EnumResourceNamesW(h, RT_STRING, (m, t, n, p) => { blocks.Add(n.ToInt64()); return true; }, IntPtr.Zero);

        int patched = 0;
        foreach (var blkId in blocks)
        {
            // patch mode: touch only zh-CN (0x0804) or neutral blocks
            var langs = new List<ushort>();
            EnumResourceLanguagesW(h, RT_STRING, new IntPtr(blkId), (m, t, n, l, p) => { langs.Add(l); return true; }, IntPtr.Zero);
            foreach (var lang in langs)
            {
                if (mode == "patch" && lang != 0x0804 && lang != 0) continue;
                var ri = FindResourceExW(h, RT_STRING, new IntPtr(blkId), lang);
                if (ri == IntPtr.Zero) continue;
                var rd = LoadResource(h, ri);
                var ptr = LockResource(rd);
                uint sz = SizeofResource(h, ri);
                var data = new byte[sz];
                Marshal.Copy(ptr, data, 0, (int)sz);
                var strs = ParseBlock(data);
                bool blockDirty = false;
                for (int i = 0; i < 16; i++)
                {
                    if (strs[i] != null && strs[i].Contains(needle))
                    {
                        uint strId = (uint)((blkId - 1) * 16 + i);
                        if (mode == "dump")
                            Console.WriteLine("block " + blkId + " lang " + lang + " id " + strId + ": [" + strs[i] + "]");
                        else if (strs[i] == needle)
                        {
                            Console.WriteLine("patch lang " + lang + " id " + strId + ": [" + strs[i] + "] -> [" + repl + "]");
                            strs[i] = repl;
                            blockDirty = true;
                            patched++;
                        }
                    }
                }
                if (mode == "patch" && blockDirty)
                {
                    var newData = BuildBlock(strs);
                    var hu = BeginUpdateResourceW(dll, false);
                    if (hu == IntPtr.Zero) { Console.WriteLine("beginupdate failed"); return 1; }
                    if (!UpdateResourceW(hu, RT_STRING, new IntPtr(blkId), lang, newData, (uint)newData.Length))
                    { Console.WriteLine("update failed"); EndUpdateResource(hu, true); return 1; }
                    if (!EndUpdateResource(hu, false)) { Console.WriteLine("endupdate failed"); return 1; }
                    Console.WriteLine("block " + blkId + " written, lang " + lang);
                }
            }
        }
        FreeLibrary(h);
        if (mode == "patch" && patched == 0) { Console.WriteLine("not found"); return 1; }
        return 0;
    }
}
