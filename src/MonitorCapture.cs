using System;
using System.Runtime.InteropServices;

namespace Morror
{
    internal sealed class MonitorCapture
    {
        internal readonly int Adapter, Output;

        MonitorCapture(int adapter, int output)
        {
            Adapter = adapter;
            Output = output;
        }

        internal static MonitorCapture Find(ScreenChoice screen)
        {
            if (string.IsNullOrEmpty(screen.DeviceName)) return null;
            Guid iid = typeof(IFactory).GUID;
            IFactory factory;
            if (CreateDXGIFactory1(ref iid, out factory) != 0) return null;
            try
            {
                for (int adapterIndex = 0; ; adapterIndex++)
                {
                    IAdapter adapter;
                    if (factory.EnumAdapters1(adapterIndex, out adapter) != 0) return null;
                    try
                    {
                        for (int outputIndex = 0; ; outputIndex++)
                        {
                            IOutput output;
                            if (adapter.EnumOutputs(outputIndex, out output) != 0) break;
                            try
                            {
                                OutputDescription description;
                                if (output.GetDesc(out description) == 0 && description.Attached &&
                                    description.Rotation == 1 &&
                                    string.Equals(description.DeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase) &&
                                    description.Left == screen.Bounds.Left && description.Right == screen.Bounds.Right &&
                                    description.Top == screen.Bounds.Top && description.Bottom == screen.Bounds.Bottom)
                                    return new MonitorCapture(adapterIndex, outputIndex);
                            }
                            finally { Marshal.ReleaseComObject(output); }
                        }
                    }
                    finally { Marshal.ReleaseComObject(adapter); }
                }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OutputDescription
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public int Left, Top, Right, Bottom;
            [MarshalAs(UnmanagedType.Bool)] public bool Attached;
            public int Rotation;
            public IntPtr Monitor;
        }

        [ComImport, Guid("770AAE78-F26F-4DBA-A829-253C83D1B387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFactory
        {
            void SetPrivateData();
            void SetPrivateDataInterface();
            void GetPrivateData();
            void GetParent();
            void EnumAdapters();
            void MakeWindowAssociation();
            void GetWindowAssociation();
            void CreateSwapChain();
            void CreateSoftwareAdapter();
            [PreserveSig] int EnumAdapters1(int index, out IAdapter adapter);
        }

        [ComImport, Guid("29038F61-3839-4626-91FD-086879011A05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAdapter
        {
            void SetPrivateData();
            void SetPrivateDataInterface();
            void GetPrivateData();
            void GetParent();
            [PreserveSig] int EnumOutputs(int index, out IOutput output);
        }

        [ComImport, Guid("AE02EEDB-C735-4690-8D52-5A8DC20213AA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IOutput
        {
            void SetPrivateData();
            void SetPrivateDataInterface();
            void GetPrivateData();
            void GetParent();
            [PreserveSig] int GetDesc(out OutputDescription description);
        }

        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid iid, out IFactory factory);
    }
}
