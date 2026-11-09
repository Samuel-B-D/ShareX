#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace ShareX.HelpersLib
{
    // Source generated COM (IShellLinkW + IPersistFile) replaces the WScript.Shell IDispatch interop.
    // [ComImport] coclasses and IDispatch interop need built-in COM, which NativeAOT does not support
    // (new WshShell() failed with InvalidProgramException).

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("000214F9-0000-0000-C000-000000000046")]
    internal unsafe partial interface IShellLinkW
    {
        [PreserveSig] int GetPath(char* pszFile, int cch, IntPtr pfd, uint fFlags);
        [PreserveSig] int GetIDList(out IntPtr ppidl);
        [PreserveSig] int SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription(char* pszName, int cch);
        [PreserveSig] int SetDescription(string pszName);
        [PreserveSig] int GetWorkingDirectory(char* pszDir, int cch);
        [PreserveSig] int SetWorkingDirectory(string pszDir);
        [PreserveSig] int GetArguments(char* pszArgs, int cch);
        [PreserveSig] int SetArguments(string pszArgs);
        [PreserveSig] int GetHotkey(out short pwHotkey);
        [PreserveSig] int SetHotkey(short wHotkey);
        [PreserveSig] int GetShowCmd(out int piShowCmd);
        [PreserveSig] int SetShowCmd(int iShowCmd);
        [PreserveSig] int GetIconLocation(char* pszIconPath, int cch, out int piIcon);
        [PreserveSig] int SetIconLocation(string pszIconPath, int iIcon);
        [PreserveSig] int SetRelativePath(string pszPathRel, uint dwReserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
        [PreserveSig] int SetPath(string pszFile);
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("0000010b-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        // IPersist
        [PreserveSig] int GetClassID(out Guid pClassID);

        // IPersistFile
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load(string pszFileName, uint dwMode);
        [PreserveSig] int Save(string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        [PreserveSig] int SaveCompleted(string pszFileName);
        [PreserveSig] int GetCurFile(out IntPtr ppszFileName);
    }

    internal static partial class ShellLink
    {
        private static readonly Guid CLSID_ShellLink = new Guid("00021401-0000-0000-C000-000000000046");
        private static readonly Guid IID_IShellLinkW = new Guid("000214F9-0000-0000-C000-000000000046");
        private const uint CLSCTX_INPROC_SERVER = 0x1;
        private const uint STGM_READ = 0x0;
        private const int MAX_PATH_LONG = 32768;

        private static readonly StrategyBasedComWrappers comWrappers = new StrategyBasedComWrappers();

        [LibraryImport("ole32.dll")]
        private static partial int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

        private static IShellLinkW Create()
        {
            Marshal.ThrowExceptionForHR(CoCreateInstance(CLSID_ShellLink, IntPtr.Zero, CLSCTX_INPROC_SERVER, IID_IShellLinkW, out IntPtr pointer));

            try
            {
                return (IShellLinkW)comWrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }

        public static void Save(string shortcutPath, string targetPath, string arguments, string workingDirectory)
        {
            IShellLinkW link = Create();
            Marshal.ThrowExceptionForHR(link.SetPath(targetPath));
            Marshal.ThrowExceptionForHR(link.SetArguments(arguments ?? string.Empty));
            Marshal.ThrowExceptionForHR(link.SetWorkingDirectory(workingDirectory ?? string.Empty));
            Marshal.ThrowExceptionForHR(((IPersistFile)link).Save(shortcutPath, true));
        }

        public static unsafe string GetTargetPath(string shortcutPath)
        {
            IShellLinkW link = Create();
            Marshal.ThrowExceptionForHR(((IPersistFile)link).Load(shortcutPath, STGM_READ));

            char[] buffer = new char[MAX_PATH_LONG];

            fixed (char* pointer = buffer)
            {
                int result = link.GetPath(pointer, buffer.Length, IntPtr.Zero, 0);
                Marshal.ThrowExceptionForHR(result);
                return result == 0 ? new string(pointer) : null;
            }
        }
    }
}
