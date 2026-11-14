using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

class SetScrcpyIcon
{
    const int Icon = 3;
    const int GroupIcon = 14;
    // The pinned scrcpy Windows package contains group 0 in language en-US.
    const int ScrcpyGroup = 0;
    const ushort ScrcpyLanguage = 1033;

    static void Main(string[] args)
    {
        using (var icon = new BinaryReader(File.OpenRead(args[1])))
        using (var group = new MemoryStream())
        using (var header = new BinaryWriter(group))
        {
            byte[] prefix = icon.ReadBytes(6);
            if (prefix.Length != 6 || BitConverter.ToUInt16(prefix, 0) != 0 || BitConverter.ToUInt16(prefix, 2) != 1)
                throw new InvalidDataException("Expected a Windows ICO.");
            int count = BitConverter.ToUInt16(prefix, 4);
            if (count == 0) throw new InvalidDataException("The ICO has no images.");
            header.Write(prefix);

            var update = BeginUpdateResource(args[0], false);
            if (update == IntPtr.Zero) throw new Win32Exception();
            bool complete = false;
            try
            {
                for (int index = 0; index < count; index++)
                {
                    icon.BaseStream.Position = 6 + 16 * index;
                    var attributes = icon.ReadBytes(12);
                    if (attributes.Length != 12) throw new InvalidDataException("Incomplete ICO directory.");
                    int size = BitConverter.ToInt32(attributes, 8);
                    int offset = icon.ReadInt32();
                    if (size <= 0 || offset < 6 + 16 * count || (long)offset + size > icon.BaseStream.Length)
                        throw new InvalidDataException("Invalid ICO image range.");
                    icon.BaseStream.Position = offset;
                    int id = index + 1;
                    Set(update, Icon, id, icon.ReadBytes(size));
                    header.Write(attributes);
                    header.Write((ushort)id);
                }
                Set(update, GroupIcon, ScrcpyGroup, group.ToArray());
                complete = true;
            }
            finally
            {
                if (!EndUpdateResource(update, !complete) && complete) throw new Win32Exception();
            }
        }
    }

    static void Set(IntPtr update, int type, int name, byte[] data)
    {
        if (!UpdateResource(update, new IntPtr(type), new IntPtr(name), ScrcpyLanguage, data, (uint)data.Length))
            throw new Win32Exception();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr BeginUpdateResource(string path, bool deleteExisting);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool UpdateResource(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool EndUpdateResource(IntPtr update, bool discard);
}
