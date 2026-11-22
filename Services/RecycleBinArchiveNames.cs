using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BannerlordEnvironmentManager.Core.Install;

namespace BannerlordEnvironmentManager.Services
{
    // Reads the names of archives the user has deleted, and nothing else. Every call here is a read:
    // the shell folder is enumerated and each item is asked for its name. Nothing is restored, moved,
    // copied or deleted, and the Recycle Bin is exactly as it was afterwards.
    public sealed class RecycleBinArchiveNames : IDeletedArchiveNames
    {
        private const int RecycleBinFolder = 10;

        public DeletedArchiveListing ReadNames()
        {
            if (!OperatingSystem.IsWindows())
                return DeletedArchiveListing.Unavailable(DeletedArchiveAvailability.NotSupported);

            var shellType = Type.GetTypeFromProgID("Shell.Application");

            if (shellType is null)
                return DeletedArchiveListing.Unavailable(DeletedArchiveAvailability.NotSupported);

            object? shell = null;
            object? bin = null;
            object? items = null;

            try
            {
                shell = Activator.CreateInstance(shellType);

                if (shell is null)
                    return DeletedArchiveListing.Unavailable(DeletedArchiveAvailability.NotReadable);

                bin = Invoke(shell, "Namespace", RecycleBinFolder);

                if (bin is null)
                    return DeletedArchiveListing.Unavailable(DeletedArchiveAvailability.NotReadable);

                items = Invoke(bin, "Items");

                if (items is null)
                    return DeletedArchiveListing.Unavailable(DeletedArchiveAvailability.NotReadable);

                return new DeletedArchiveListing(ArchiveNames(items));
            }
            catch (Exception ex) when (ex is COMException
                or UnauthorizedAccessException
                or MissingMemberException
                or InvalidCastException
                or TargetInvocationException
                or NotSupportedException
                or PlatformNotSupportedException)
            {
                return DeletedArchiveListing.Unavailable(DeletedArchiveAvailability.NotReadable);
            }
            finally
            {
                Release(items);
                Release(bin);
                Release(shell);
            }
        }

        private static List<string> ArchiveNames(object items)
        {
            var names = new List<string>();
            var count = Property(items, "Count") as int? ?? 0;

            for (var i = 0; i < count; i++)
            {
                var item = Invoke(items, "Item", i);

                if (item is null)
                    continue;

                try
                {
                    if (Property(item, "Name") is string name && IsArchive(name))
                        names.Add(name);
                }
                finally
                {
                    Release(item);
                }
            }

            return names;
        }

        private static bool IsArchive(string name) => ArchiveExtensions.IsArchive(Path.GetExtension(name));

        private static object? Invoke(object target, string member, params object[] arguments) =>
            target.GetType().InvokeMember(member, BindingFlags.InvokeMethod, null, target, arguments);

        private static object? Property(object target, string member) =>
            target.GetType().InvokeMember(member, BindingFlags.GetProperty, null, target, null);

        private static void Release(object? comObject)
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
                Marshal.FinalReleaseComObject(comObject);
        }
    }
}
