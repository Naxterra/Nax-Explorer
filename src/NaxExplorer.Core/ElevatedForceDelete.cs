using System.IO.Enumeration;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace NaxExplorer.Core;

/// <summary>Why a path is refused for elevated ownership takeover and forced permanent deletion.</summary>
public enum ForceDeleteBlockKind
{
    None,
    UnsupportedPath,
    CloudPath,
    SystemProtected,
    AppxVolumeHasPackages,
    AppxVolumeNotEmpty,
    NotElevated,
}

/// <summary>One item failure reported by the elevated force-delete helper.</summary>
public sealed record ForceDeleteItemError(
    string Path,
    string Name,
    int Win32Error,
    ForceDeleteBlockKind BlockKind = ForceDeleteBlockKind.None,
    string? Detail = null);

/// <summary>JSON request sent over a private named pipe to the UAC-elevated Nax-Explorer helper process.</summary>
public sealed record ForceDeleteRequest(
    List<string> Paths,
    List<string>? ExtraCloudRoots = null,
    string? RequestingUserProfile = null);

/// <summary>JSON result returned over the named pipe by the UAC-elevated Nax-Explorer helper process.</summary>
public sealed record ForceDeleteResponse(List<ForceDeleteItemError> Errors, bool Cancelled = false);

/// <summary>
/// Safe, system-guarded ownership takeover and permanent deletion for NTFS ACL-locked items (such as TrustedInstaller
/// leftover folders on secondary drives) after an initial delete fails with Access Denied and the user explicitly
/// confirms forced permanent deletion plus Windows UAC.
/// </summary>
public static class ElevatedForceDelete
{
    public const string WorkerSwitch = "--elevated-force-delete";
    public const string CancelCommand = "CANCEL";

    private const uint InvalidFileAttributes = 0xFFFFFFFF;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileReadAttributes = 0x00000080;
    private const uint WriteOwner = 0x00080000;
    private const uint WriteDac = 0x00040000;
    private const uint FileShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private const uint GenericAll = 0x10000000;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const uint VolumeNameDos = 0x0;
    private const int FileBasicInfo = 0;
    private const int FileAttributeTagInfo = 9;
    private const int SeFileObject = 1;
    private const int SetAccess = 2;
    private const int TrusteeIsSid = 0;
    private const int TrusteeIsWellKnownGroup = 5;
    private const int WinBuiltinAdministratorsSid = 26;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotSupported = 50;
    private const int ErrorPrivilegeNotHeld = 1314;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    internal sealed record ProtectedEnvironment(
        string SystemDriveRoot,
        string WindowsDir,
        string ProgramFilesDir,
        string? ProgramFilesX86Dir,
        string ProgramDataDir,
        string UsersDir,
        IReadOnlyList<string> UserProfileDirs);

    internal static int TrusteeStructSize => Marshal.SizeOf<TRUSTEE_W>();
    internal static int ExplicitAccessStructSize => Marshal.SizeOf<EXPLICIT_ACCESS_W>();

    /// <summary>True when the current process runs with an elevated local Administrator token.</summary>
    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or SystemException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is eligible for explicit Administrator ownership takeover and forced permanent
    /// deletion. Refuses extended/device prefixes, drive roots, core OS folders (including Boot/EFI/Recovery and
    /// system-drive WindowsApps subtrees), system volume metadata folders on any drive, shell places, ZIP entries,
    /// network paths, trailing-dot paths, and active cloud sync roots.
    /// </summary>
    public static bool IsEligible(string? path, Func<string, bool>? isCloudPath, out ForceDeleteBlockKind reason) =>
        IsEligible(path, isCloudPath, CurrentProtectedEnvironment(extraUserProfile: null), out reason);

    internal static bool IsEligible(string? path, Func<string, bool>? isCloudPath, ProtectedEnvironment env, out ForceDeleteBlockKind reason)
    {
        reason = ForceDeleteBlockKind.UnsupportedPath;
        if (string.IsNullOrWhiteSpace(path)
            || PathTools.IsShellPlace(path)
            || CompressedFolders.TrySplit(path, out _, out _))
            return false;

        string slashed = path.Replace('/', '\\');
        if (slashed.StartsWith(@"\\?\", StringComparison.Ordinal)
            || slashed.StartsWith(@"\\.\", StringComparison.Ordinal)
            || !IsDriveLetterPath(slashed)
            || slashed.IndexOf(@"\\", 2, StringComparison.Ordinal) >= 0
            || FolderStats.IsNetworkPath(slashed)
            || FastPermanentDelete.HasTrailingDotOrSpaceSegment(slashed)
            || !FastPermanentDelete.TryToExtendedPath(slashed, out _))
            return false;

        string normalized = NormalizeLocalPath(slashed);
        if (!CheckLexicalEligibility(normalized, isCloudPath, env, out reason))
            return false;

        // If the path (or an existing ancestor) resolves through 8.3 short names, SUBST drives, or ancestor junctions,
        // verify the canonical filesystem path against every protected-path and cloud-root guard as well.
        if (TryResolveCanonicalPath(normalized, out string canonical) && !PathTools.SameLocation(canonical, normalized))
        {
            if (!IsDriveLetterPath(canonical) || FolderStats.IsNetworkPath(canonical))
            {
                reason = ForceDeleteBlockKind.UnsupportedPath;
                return false;
            }
            if (!CheckLexicalEligibility(canonical, isCloudPath, env, out reason))
                return false;
        }

        reason = ForceDeleteBlockKind.None;
        return true;
    }

    private static bool IsDriveLetterPath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    private static bool CheckLexicalEligibility(string normalized, Func<string, bool>? isCloudPath, ProtectedEnvironment env, out ForceDeleteBlockKind reason)
    {
        reason = ForceDeleteBlockKind.UnsupportedPath;
        if (!IsDriveLetterPath(normalized) || normalized.IndexOf(@"\\", 2, StringComparison.Ordinal) >= 0) return false;
        string? root;
        try { root = Path.GetPathRoot(normalized); }
        catch (ArgumentException) { return false; }
        if (string.IsNullOrEmpty(root) || PathTools.SameLocation(normalized, root))
        {
            reason = ForceDeleteBlockKind.SystemProtected;
            return false;
        }

        if (isCloudPath?.Invoke(normalized) == true)
        {
            reason = ForceDeleteBlockKind.CloudPath;
            return false;
        }

        // Block System Volume Information and $Recycle.Bin (and anything inside them) on every drive.
        string rootTrimmed = root.TrimEnd('\\');
        if (PathTools.IsSameOrInside(normalized, rootTrimmed + @"\System Volume Information")
            || PathTools.IsSameOrInside(normalized, rootTrimmed + @"\$Recycle.Bin"))
        {
            reason = ForceDeleteBlockKind.SystemProtected;
            return false;
        }

        // Block Windows, Boot, EFI, Recovery, and system-drive WindowsApps / ModifiableWindowsApps including all descendants.
        string sysRoot = env.SystemDriveRoot.TrimEnd('\\');
        if (OverlapsProtectedTree(normalized, env.WindowsDir)
            || OverlapsProtectedTree(normalized, sysRoot + @"\Recovery")
            || OverlapsProtectedTree(normalized, sysRoot + @"\EFI")
            || OverlapsProtectedTree(normalized, sysRoot + @"\Boot"))
        {
            reason = ForceDeleteBlockKind.SystemProtected;
            return false;
        }
        if (!string.IsNullOrWhiteSpace(env.ProgramFilesDir))
        {
            string sysWindowsApps = Path.Combine(env.ProgramFilesDir, "WindowsApps");
            string sysModifiable = Path.Combine(env.ProgramFilesDir, "ModifiableWindowsApps");
            if (OverlapsProtectedTree(normalized, sysWindowsApps) || OverlapsProtectedTree(normalized, sysModifiable))
            {
                reason = ForceDeleteBlockKind.SystemProtected;
                return false;
            }
        }

        // Block deleting core top-level OS directories or any user profile root themselves (or an ancestor containing them).
        var protectedRoots = new List<string?>
        {
            env.ProgramFilesDir,
            env.ProgramFilesX86Dir,
            env.ProgramDataDir,
            env.UsersDir,
        };
        foreach (string profile in env.UserProfileDirs) protectedRoots.Add(profile);
        foreach (string? guarded in protectedRoots)
        {
            if (string.IsNullOrWhiteSpace(guarded)) continue;
            if (PathTools.SameLocation(normalized, guarded) || PathTools.IsSameOrInside(guarded, normalized))
            {
                reason = ForceDeleteBlockKind.SystemProtected;
                return false;
            }
        }

        reason = ForceDeleteBlockKind.None;
        return true;
    }

    /// <summary>
    /// Maps access-denied failure paths from a delete operation back to the eligible top-level requested paths so a
    /// nested locked child (such as ModifiableWindowsApps inside E:\Program Files) offers takeover for the whole folder
    /// the user chose to delete.
    /// </summary>
    public static List<string> MapEligibleFailurePaths(
        IReadOnlyList<string> requestedPaths,
        IEnumerable<(string? Path, bool IsAccessDenied)> failures,
        Func<string, bool>? isCloudPath)
    {
        var eligible = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (failedPath, isAccessDenied) in failures)
        {
            if (!isAccessDenied || string.IsNullOrWhiteSpace(failedPath)) continue;
            string mapped = requestedPaths.FirstOrDefault(req => !PathTools.IsShellPlace(req) && PathTools.IsSameOrInside(failedPath, req)) ?? failedPath;
            if (!IsEligible(mapped, isCloudPath, out _)) continue;
            string key = mapped.TrimEnd('\\');
            if (seen.Add(key)) eligible.Add(mapped);
        }
        return eligible;
    }

    /// <summary>
    /// Entry point for the headless UAC-elevated helper (`Nax-Explorer.exe --elevated-force-delete <pipeName>`).
    /// Connects to the caller's private named pipe, receives the request, listens for cancellation on the same duplex
    /// pipe, re-validates every path against canonical protected-path rules, all users' cloud roots, and Appx volumes,
    /// performs ownership takeover and deletion, and writes the JSON response back over the pipe.
    /// </summary>
    public static int RunElevatedWorker(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.IndexOfAny(['\\', '/', '\0']) >= 0)
            return 2;
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(15000);
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            using var reader = new StreamReader(pipe, utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, utf8NoBom, bufferSize: 4096, leaveOpen: true) { AutoFlush = true };

            string? reqJson = reader.ReadLineAsync().GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(reqJson))
            {
                writer.WriteLineAsync(JsonSerializer.Serialize(new ForceDeleteResponse([]))).GetAwaiter().GetResult();
                return 0;
            }
            ForceDeleteRequest? request = JsonSerializer.Deserialize<ForceDeleteRequest>(reqJson);
            if (request is null || request.Paths.Count == 0)
            {
                writer.WriteLineAsync(JsonSerializer.Serialize(new ForceDeleteResponse([]))).GetAwaiter().GetResult();
                return 0;
            }

            if (!IsCurrentProcessElevated())
            {
                List<ForceDeleteItemError> denied = request.Paths
                    .Select(p => new ForceDeleteItemError(p, DisplayName(p), ErrorAccessDenied, ForceDeleteBlockKind.NotElevated))
                    .ToList();
                writer.WriteLineAsync(JsonSerializer.Serialize(new ForceDeleteResponse(denied))).GetAwaiter().GetResult();
                return 1;
            }

            using var cancelCts = new CancellationTokenSource();
            Task cancelWatcher = Task.Run(async () =>
            {
                try
                {
                    while (!cancelCts.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync(cancelCts.Token);
                        if (line is null || string.Equals(line.Trim(), CancelCommand, StringComparison.OrdinalIgnoreCase))
                        {
                            cancelCts.Cancel();
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { cancelCts.Cancel(); }
            });

            // Build an additive union of cloud roots across all Windows users, elevated drives, and the requesting user.
            var allCloudPaths = new List<string>();
            foreach (CloudFolder f in CloudFolders.FindAllUsersSyncRoots()) allCloudPaths.Add(f.Path);
            foreach (CloudFolder f in CloudFolders.Find(withDrives: true)) allCloudPaths.Add(f.Path);
            if (request.ExtraCloudRoots is not null)
            {
                foreach (string extra in request.ExtraCloudRoots)
                {
                    if (!string.IsNullOrWhiteSpace(extra) && !PathTools.IsShellPlace(extra)) allCloudPaths.Add(extra);
                }
            }
            bool IsCloud(string p) => allCloudPaths.Any(root => PathTools.IsSameOrInside(p, root));

            ProtectedEnvironment env = CurrentProtectedEnvironment(request.RequestingUserProfile);
            ForceDeleteResponse response = TakeOwnershipAndDelete(
                request.Paths,
                IsCloud,
                appxVolumeGuard: null,
                isCancelled: () => cancelCts.IsCancellationRequested,
                protectedEnv: env);

            try { writer.WriteLineAsync(JsonSerializer.Serialize(response)).GetAwaiter().GetResult(); }
            catch (IOException) { }
            cancelCts.Cancel();
            try { cancelWatcher.Wait(TimeSpan.FromMilliseconds(250)); }
            catch (AggregateException) { }
            return response.Errors.Count == 0 && !response.Cancelled ? 0 : 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return 1;
        }
    }

    /// <summary>
    /// Enables required Win32 backup/restore/take-ownership privileges, verifies canonical path and Appx volume safety,
    /// assigns ownership and Administrators Full Control without following symlinks/junctions or touching cloud
    /// placeholders, and deletes each eligible target through <see cref="FastPermanentDelete"/>.
    /// </summary>
    public static ForceDeleteResponse TakeOwnershipAndDelete(
        IReadOnlyList<string> paths,
        Func<string, bool>? isCloudPath,
        Func<string, ForceDeleteItemError?>? appxVolumeGuard = null,
        Func<bool>? isCancelled = null) =>
        TakeOwnershipAndDelete(paths, isCloudPath, appxVolumeGuard, isCancelled, protectedEnv: null);

    internal static ForceDeleteResponse TakeOwnershipAndDelete(
        IReadOnlyList<string> paths,
        Func<string, bool>? isCloudPath,
        Func<string, ForceDeleteItemError?>? appxVolumeGuard,
        Func<bool>? isCancelled,
        ProtectedEnvironment? protectedEnv)
    {
        var errors = new List<ForceDeleteItemError>();
        Func<bool> cancelled = isCancelled ?? (() => false);
        ProtectedEnvironment env = protectedEnv ?? CurrentProtectedEnvironment(extraUserProfile: null);

        EnablePrivilege("SeTakeOwnershipPrivilege");
        EnablePrivilege("SeBackupPrivilege");
        EnablePrivilege("SeRestorePrivilege");

        IntPtr adminSid = CreateAdministratorsSid();
        IntPtr adminDacl = IntPtr.Zero;
        try
        {
            if (adminSid == IntPtr.Zero)
            {
                foreach (string p in paths)
                    errors.Add(new ForceDeleteItemError(p, DisplayName(p), Marshal.GetLastWin32Error()));
                return new ForceDeleteResponse(errors);
            }

            adminDacl = CreateFullControlDacl(adminSid);
            if (adminDacl == IntPtr.Zero)
            {
                foreach (string p in paths)
                    errors.Add(new ForceDeleteItemError(p, DisplayName(p), ErrorAccessDenied));
                return new ForceDeleteResponse(errors);
            }

            var readyToDelete = new List<string>(paths.Count);
            foreach (string path in paths)
            {
                if (cancelled()) return new ForceDeleteResponse(errors, Cancelled: true);
                if (!IsEligible(path, isCloudPath, env, out ForceDeleteBlockKind blockReason))
                {
                    errors.Add(new ForceDeleteItemError(path, DisplayName(path), ErrorAccessDenied, blockReason));
                    continue;
                }

                string normalized = NormalizeLocalPath(path);
                if (!FastPermanentDelete.TryToExtendedPath(normalized, out string extPath))
                {
                    errors.Add(new ForceDeleteItemError(path, DisplayName(path), ErrorNotSupported, ForceDeleteBlockKind.UnsupportedPath));
                    continue;
                }

                // Resolve canonical path (with SeBackupPrivilege already active) so short names, SUBST drives, and
                // ancestor junctions cannot bypass protected-path, cloud-root, or Appx-volume checks. Fail closed when
                // a DOS drive-letter canonical path cannot be obtained (for example volumes mounted without a drive letter).
                if (TryInspectItemReadOnly(extPath, out FileAttributes rootAttrs, out uint rootReparseTag, out string? resolvedCanonical, out int inspectErr))
                {
                    if (string.IsNullOrWhiteSpace(resolvedCanonical))
                    {
                        errors.Add(new ForceDeleteItemError(path, DisplayName(path), ErrorNotSupported, ForceDeleteBlockKind.UnsupportedPath));
                        continue;
                    }
                    string canonicalTarget = resolvedCanonical;
                    if (!IsEligible(canonicalTarget, isCloudPath, env, out ForceDeleteBlockKind canonicalBlock))
                    {
                        errors.Add(new ForceDeleteItemError(path, DisplayName(path), ErrorAccessDenied, canonicalBlock));
                        continue;
                    }
                    ForceDeleteBlockKind reparseBlock = ClassifyForbiddenItem(rootAttrs, rootReparseTag, out bool rootIsLink);
                    if (reparseBlock != ForceDeleteBlockKind.None)
                    {
                        errors.Add(new ForceDeleteItemError(path, DisplayName(path), ErrorNotSupported, reparseBlock));
                        continue;
                    }

                    ForceDeleteItemError? appxError = (appxVolumeGuard ?? TryPrepareAppxVolume)(canonicalTarget);
                    if (appxError is null && !PathTools.SameLocation(canonicalTarget, normalized))
                        appxError = (appxVolumeGuard ?? TryPrepareAppxVolume)(normalized);
                    if (appxError is not null)
                    {
                        errors.Add(appxError);
                        continue;
                    }

                    if (!FastPermanentDelete.TryToExtendedPath(canonicalTarget, out string canonicalExt))
                    {
                        errors.Add(new ForceDeleteItemError(path, DisplayName(path), ErrorNotSupported, ForceDeleteBlockKind.UnsupportedPath));
                        continue;
                    }

                    bool safeToDelete = PrepareTreeForDeletion(canonicalExt, canonicalTarget, rootAttrs, rootIsLink, isCloudPath, adminSid, adminDacl, errors, cancelled);
                    if (cancelled()) return new ForceDeleteResponse(errors, Cancelled: true);
                    if (safeToDelete) readyToDelete.Add(canonicalTarget);
                }
                else
                {
                    if (inspectErr is ErrorFileNotFound or ErrorPathNotFound) continue;
                    errors.Add(new ForceDeleteItemError(path, DisplayName(path), inspectErr != 0 ? inspectErr : ErrorAccessDenied));
                }
            }

            if (!cancelled() && readyToDelete.Count > 0)
            {
                FastDeleteResult fast = FastPermanentDelete.Delete(readyToDelete, isCloudPath, cancelled);
                foreach (FastDeleteFailure failure in fast.Failures)
                {
                    errors.Add(new ForceDeleteItemError(failure.Path, failure.Name, failure.Win32Error));
                }
                foreach (string deferred in fast.FallbackToShell)
                {
                    if (FastPermanentDelete.ExistsExact(deferred)
                        && !errors.Any(e => string.Equals(e.Path.TrimEnd('\\'), deferred.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add(new ForceDeleteItemError(deferred, DisplayName(deferred), ErrorAccessDenied));
                    }
                }
                if (!fast.Cancelled && fast.DeferredParents.Count > 0)
                {
                    var (needsElev, parentFailures) = FastPermanentDelete.TryRemoveEmptyDirectories(fast.DeferredParents, priorFailuresExist: errors.Count > 0);
                    foreach (FastDeleteFailure failure in parentFailures)
                    {
                        errors.Add(new ForceDeleteItemError(failure.Path, failure.Name, failure.Win32Error));
                    }
                    foreach (string elev in needsElev)
                    {
                        if (FastPermanentDelete.ExistsExact(elev))
                            errors.Add(new ForceDeleteItemError(elev, DisplayName(elev), ErrorAccessDenied));
                    }
                }
                if (fast.Cancelled) return new ForceDeleteResponse(errors, Cancelled: true);
            }

            return new ForceDeleteResponse(errors, Cancelled: cancelled());
        }
        finally
        {
            if (adminDacl != IntPtr.Zero) LocalFree(adminDacl);
            if (adminSid != IntPtr.Zero) Marshal.FreeHGlobal(adminSid);
        }
    }

    /// <summary>
    /// Checks whether <paramref name="path"/> overlaps a registered non-system Windows Store/Xbox Appx volume. If
    /// packages remain on that volume, returns a blocking error naming them; if the volume is empty, deregisters it
    /// cleanly via <see cref="PackageManager.RemovePackageVolumeAsync"/> before deleting any leftover folder stub.
    /// Fails closed whenever an exception occurs after matching a volume or on any path containing a WindowsApps segment.
    /// </summary>
    private static ForceDeleteItemError? TryPrepareAppxVolume(string path)
    {
        bool matchedVolume = false;
        try
        {
            var manager = new PackageManager();
            foreach (PackageVolume volume in manager.FindPackageVolumes())
            {
                string? storePath = volume.PackageStorePath;
                if (string.IsNullOrWhiteSpace(storePath)) continue;
                if (!PathTools.IsSameOrInside(path, storePath) && !PathTools.IsSameOrInside(storePath, path)) continue;
                matchedVolume = true;
                if (volume.IsSystemVolume)
                {
                    return new ForceDeleteItemError(path, DisplayName(path), ErrorAccessDenied, ForceDeleteBlockKind.SystemProtected);
                }

                IList<Package> packages = volume.FindPackages();
                if (packages.Count == 0)
                {
                    packages = volume.FindPackagesForUser("");
                }
                if (packages.Count > 0)
                {
                    string names = string.Join(", ", packages.Select(p => p.Id.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
                    return new ForceDeleteItemError(path, DisplayName(path), ErrorAccessDenied, ForceDeleteBlockKind.AppxVolumeHasPackages, names);
                }

                DeploymentResult removeResult = manager.RemovePackageVolumeAsync(volume).AsTask().GetAwaiter().GetResult();
                if (removeResult.ExtendedErrorCode is not null)
                {
                    return new ForceDeleteItemError(path, DisplayName(path), ErrorAccessDenied, ForceDeleteBlockKind.AppxVolumeNotEmpty, removeResult.ErrorText);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException)
        {
            if (matchedVolume || HasSegment(path, "WindowsApps"))
            {
                return new ForceDeleteItemError(path, DisplayName(path), ErrorAccessDenied, ForceDeleteBlockKind.AppxVolumeNotEmpty, ex.Message);
            }
        }
        return null;
    }

    private static bool PrepareTreeForDeletion(
        string rootExtPath,
        string canonicalRoot,
        FileAttributes rootAttrs,
        bool rootIsLink,
        Func<string, bool>? isCloudPath,
        IntPtr adminSid,
        IntPtr adminDacl,
        List<ForceDeleteItemError> errors,
        Func<bool> isCancelled)
    {
        if (isCancelled()) return false;
        GrantOwnershipAndDacl(rootExtPath, adminSid, adminDacl);
        ClearReadOnlyIfSet(rootExtPath, rootAttrs);

        if ((rootAttrs & FileAttributes.Directory) == 0 || rootIsLink)
            return true;

        var pending = new Stack<(string ExtPath, string NormalPath)>();
        pending.Push((rootExtPath, canonicalRoot));
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        while (pending.TryPop(out var current))
        {
            if (isCancelled()) return false;

            // Re-verify right before enumerating that the queued directory was not swapped for a junction/symlink
            // pointing outside canonicalRoot.
            if (!TryInspectItemReadOnly(current.ExtPath, out FileAttributes dirAttrs, out uint dirTag, out string? dirCanonical, out int dirErr))
            {
                if (dirErr is not (ErrorFileNotFound or ErrorPathNotFound))
                    errors.Add(new ForceDeleteItemError(current.NormalPath, DisplayName(current.NormalPath), dirErr != 0 ? dirErr : ErrorAccessDenied));
                continue;
            }
            if ((dirAttrs & FileAttributes.Directory) == 0
                || (dirAttrs & FileAttributes.ReparsePoint) != 0
                || ClassifyForbiddenItem(dirAttrs, dirTag, out _) != ForceDeleteBlockKind.None
                || string.IsNullOrWhiteSpace(dirCanonical)
                || !PathTools.IsSameOrInside(dirCanonical, canonicalRoot))
            {
                continue;
            }

            try
            {
                var entries = new FileSystemEnumerable<(string ExtPath, bool IsDir, FileAttributes Attrs)>(
                    current.ExtPath,
                    (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory, entry.Attributes),
                    options);
                foreach (var entry in entries)
                {
                    if (isCancelled()) return false;
                    string childNormal = FastPermanentDelete.StripExtendedPrefix(entry.ExtPath);
                    if (isCloudPath?.Invoke(childNormal) == true)
                    {
                        errors.Add(new ForceDeleteItemError(childNormal, DisplayName(childNormal), ErrorNotSupported, ForceDeleteBlockKind.CloudPath));
                        continue;
                    }
                    uint childTag = 0;
                    if ((entry.Attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        if (!FastPermanentDelete.TryReadReparseTag(entry.ExtPath, out childTag))
                        {
                            errors.Add(new ForceDeleteItemError(childNormal, DisplayName(childNormal), ErrorNotSupported, ForceDeleteBlockKind.UnsupportedPath));
                            continue;
                        }
                    }
                    ForceDeleteBlockKind childBlock = ClassifyForbiddenItem(entry.Attrs, childTag, out bool isLink);
                    if (childBlock != ForceDeleteBlockKind.None)
                    {
                        errors.Add(new ForceDeleteItemError(childNormal, DisplayName(childNormal), ErrorNotSupported, childBlock));
                        continue;
                    }
                    GrantOwnershipAndDacl(entry.ExtPath, adminSid, adminDacl);
                    ClearReadOnlyIfSet(entry.ExtPath, entry.Attrs);
                    if (entry.IsDir && !isLink)
                    {
                        pending.Push((entry.ExtPath, childNormal));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
            {
                errors.Add(new ForceDeleteItemError(current.NormalPath, DisplayName(current.NormalPath), ErrorAccessDenied));
            }
        }

        return FastPermanentDelete.ExistsExact(canonicalRoot);
    }

    internal static ForceDeleteBlockKind ClassifyForbiddenItem(FileAttributes attrs, uint reparseTag, out bool isLink)
    {
        isLink = false;
        if (CloudFiles.IsOnlineOnly(attrs) || CloudFiles.IsUnfetchedFolder(attrs))
            return ForceDeleteBlockKind.CloudPath;
        if ((attrs & FileAttributes.ReparsePoint) == 0)
            return ForceDeleteBlockKind.None;
        if (reparseTag is FastPermanentDelete.IoReparseTagSymlink or FastPermanentDelete.IoReparseTagMountPoint)
        {
            isLink = true;
            return ForceDeleteBlockKind.None;
        }
        // Cloud Files filter reparse tags share 0x9000001A (with bits 12..15 distinguishing IO_REPARSE_TAG_CLOUD_1..F).
        if ((reparseTag & 0xFFFF0FFFu) == 0x9000001Au)
            return ForceDeleteBlockKind.CloudPath;
        return ForceDeleteBlockKind.UnsupportedPath;
    }

    /// <summary>
    /// Inspects attributes, reparse tag, and canonical DOS path read-only via FILE_FLAG_OPEN_REPARSE_POINT and
    /// FILE_FLAG_BACKUP_SEMANTICS without modifying any owner or DACL.
    /// </summary>
    private static bool TryInspectItemReadOnly(
        string extPath,
        out FileAttributes attrs,
        out uint reparseTag,
        out string? canonicalPath,
        out int win32Error)
    {
        attrs = 0;
        reparseTag = 0;
        canonicalPath = null;
        win32Error = 0;

        IntPtr handle = CreateFileW(extPath, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle == InvalidHandleValue)
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }
        try
        {
            if (GetFileInformationByHandleExBasic(handle, FileBasicInfo, out FILE_BASIC_INFO basic, (uint)Marshal.SizeOf<FILE_BASIC_INFO>()))
            {
                attrs = (FileAttributes)basic.FileAttributes;
            }
            else
            {
                uint raw = GetFileAttributesW(extPath);
                if (raw == InvalidFileAttributes)
                {
                    win32Error = Marshal.GetLastWin32Error();
                    return false;
                }
                attrs = (FileAttributes)raw;
            }

            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                if (GetFileInformationByHandleExTag(handle, FileAttributeTagInfo, out FILE_ATTRIBUTE_TAG_INFO tagInfo, (uint)Marshal.SizeOf<FILE_ATTRIBUTE_TAG_INFO>()))
                {
                    reparseTag = tagInfo.ReparseTag;
                }
                else
                {
                    FastPermanentDelete.TryReadReparseTag(extPath, out reparseTag);
                }
            }

            canonicalPath = ReadFinalDosPath(handle);
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool TryResolveCanonicalPath(string path, out string canonicalPath)
    {
        canonicalPath = path;
        if (!FastPermanentDelete.TryToExtendedPath(path, out string extPath)) return false;

        // First try the item itself (without following a final symlink/junction, resolving all ancestor junctions,
        // SUBST drives, and 8.3 short names).
        IntPtr handle = CreateFileW(extPath, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle != InvalidHandleValue)
        {
            try
            {
                if (ReadFinalDosPath(handle) is { } resolved)
                {
                    canonicalPath = resolved;
                    return true;
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        // If the target does not exist yet or cannot be opened without privileges, walk up to the deepest existing
        // ancestor and canonicalize the prefix. Follow genuine ancestor symlinks/junctions so links into protected
        // system paths resolve to their real target, while never following cloud or special reparse points.
        var tail = new Stack<string>();
        string current = path.TrimEnd('\\');
        while (current.Length > 3)
        {
            int slash = current.LastIndexOf('\\');
            if (slash <= 1) break;
            tail.Push(current[(slash + 1)..]);
            current = current[..slash];
            if (current.Length == 2 && current[1] == ':') current += '\\';
            if (!FastPermanentDelete.TryToExtendedPath(current, out string ancestorExt)) break;
            uint openFlags = FileFlagBackupSemantics | FileFlagOpenReparsePoint;
            if (TryInspectItemReadOnly(ancestorExt, out FileAttributes ancAttrs, out uint ancTag, out _, out _)
                && (ancAttrs & FileAttributes.ReparsePoint) != 0
                && ancTag is FastPermanentDelete.IoReparseTagSymlink or FastPermanentDelete.IoReparseTagMountPoint)
            {
                openFlags = FileFlagBackupSemantics;
            }
            IntPtr ancHandle = CreateFileW(ancestorExt, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, openFlags, IntPtr.Zero);
            if (ancHandle == InvalidHandleValue) continue;
            try
            {
                if (ReadFinalDosPath(ancHandle) is { } resolvedAncestor)
                {
                    string rebuilt = resolvedAncestor.TrimEnd('\\');
                    while (tail.Count > 0) rebuilt += "\\" + tail.Pop();
                    canonicalPath = NormalizeLocalPath(rebuilt);
                    return true;
                }
            }
            finally
            {
                CloseHandle(ancHandle);
            }
        }
        return false;
    }

    private static string? ReadFinalDosPath(IntPtr handle)
    {
        uint needed = GetFinalPathNameByHandleW(handle, null, 0, VolumeNameDos);
        if (needed == 0 || needed > 32768) return null;
        char[] buffer = new char[needed + 1];
        uint written = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, VolumeNameDos);
        if (written == 0 || written >= buffer.Length) return null;
        string raw = new(buffer, 0, (int)written);
        string stripped = FastPermanentDelete.StripExtendedPrefix(raw);
        return NormalizeLocalPath(stripped);
    }

    internal static void GrantOwnershipAndDacl(string extPath, IntPtr ownerSid, IntPtr dacl)
    {
        const uint flags = FileFlagBackupSemantics | FileFlagOpenReparsePoint;
        IntPtr ownerHandle = CreateFileW(extPath, WriteOwner, FileShareAll, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
        if (ownerHandle != InvalidHandleValue)
        {
            try
            {
                _ = SetSecurityInfo(ownerHandle, SeFileObject, OwnerSecurityInformation, ownerSid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                CloseHandle(ownerHandle);
            }
        }

        IntPtr daclHandle = CreateFileW(extPath, WriteDac, FileShareAll, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
        if (daclHandle != InvalidHandleValue)
        {
            try
            {
                _ = SetSecurityInfo(daclHandle, SeFileObject, DaclSecurityInformation | ProtectedDaclSecurityInformation, IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
            }
            finally
            {
                CloseHandle(daclHandle);
            }
        }
    }

    private static void ClearReadOnlyIfSet(string extPath, FileAttributes attrs)
    {
        if ((attrs & FileAttributes.ReadOnly) == 0) return;
        uint next = (uint)(attrs & ~FileAttributes.ReadOnly);
        if (next == 0) next = FileAttributeNormal;
        _ = SetFileAttributesW(extPath, next);
    }

    private static bool EnablePrivilege(string privilegeName)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out IntPtr token))
            return false;
        try
        {
            if (!LookupPrivilegeValueW(null, privilegeName, out LUID luid))
                return false;
            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SePrivilegeEnabled },
            };
            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static IntPtr CreateAdministratorsSid()
    {
        uint size = 68; // SECURITY_MAX_SID_SIZE
        IntPtr sid = Marshal.AllocHGlobal((int)size);
        if (!CreateWellKnownSid(WinBuiltinAdministratorsSid, IntPtr.Zero, sid, ref size))
        {
            Marshal.FreeHGlobal(sid);
            return IntPtr.Zero;
        }
        return sid;
    }

    internal static IntPtr CreateFullControlDacl(IntPtr sid)
    {
        var access = new EXPLICIT_ACCESS_W
        {
            grfAccessPermissions = GenericAll,
            grfAccessMode = SetAccess,
            grfInheritance = 0,
            Trustee = new TRUSTEE_W
            {
                pMultipleTrustee = IntPtr.Zero,
                MultipleTrusteeOperation = 0,
                TrusteeForm = TrusteeIsSid,
                TrusteeType = TrusteeIsWellKnownGroup,
                ptstrName = sid,
            },
        };
        return SetEntriesInAclW(1, ref access, IntPtr.Zero, out IntPtr newAcl) == 0 ? newAcl : IntPtr.Zero;
    }

    private static bool HasSegment(string path, string segment)
    {
        foreach (string part in path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(part, segment, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string NormalizeLocalPath(string path)
    {
        string normalized = path.Replace('/', '\\').TrimEnd('\\');
        return normalized.Length == 2 && normalized[1] == ':' ? normalized + '\\' : normalized;
    }

    private static bool OverlapsProtectedTree(string candidate, string protectedDir) =>
        !string.IsNullOrWhiteSpace(protectedDir)
        && (PathTools.IsSameOrInside(candidate, protectedDir) || PathTools.IsSameOrInside(protectedDir, candidate));

    private static ProtectedEnvironment CurrentProtectedEnvironment(string? extraUserProfile)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string sysRoot = Path.GetPathRoot(string.IsNullOrEmpty(windows) ? Environment.SystemDirectory : windows) ?? @"C:\";
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string pdata = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string users = Path.GetDirectoryName(profile.TrimEnd('\\')) ?? Path.Combine(sysRoot, "Users");

        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(profile)) profiles.Add(profile);
        if (!string.IsNullOrWhiteSpace(extraUserProfile) && !PathTools.IsShellPlace(extraUserProfile)) profiles.Add(extraUserProfile);
        try
        {
            using RegistryKey? list = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (list is not null)
            {
                foreach (string sub in list.GetSubKeyNames())
                {
                    using RegistryKey? key = list.OpenSubKey(sub);
                    if (key?.GetValue("ProfileImagePath") is string p && !string.IsNullOrWhiteSpace(p))
                        profiles.Add(Environment.ExpandEnvironmentVariables(p));
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return new ProtectedEnvironment(sysRoot, windows, pf, pf86, pdata, users, [.. profiles]);
    }

    private static string DisplayName(string path)
    {
        string trimmed = path.TrimEnd('\\');
        int slash = trimmed.LastIndexOf('\\');
        return slash >= 0 && slash + 1 < trimmed.Length ? trimmed[(slash + 1)..] : trimmed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TRUSTEE_W
    {
        public IntPtr pMultipleTrustee;
        public int MultipleTrusteeOperation;
        public int TrusteeForm;
        public int TrusteeType;
        public IntPtr ptstrName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EXPLICIT_ACCESS_W
    {
        public uint grfAccessPermissions;
        public int grfAccessMode;
        public uint grfInheritance;
        public TRUSTEE_W Trustee;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateWellKnownSid(int WellKnownSidType, IntPtr DomainSid, IntPtr pSid, ref uint cbSid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SetEntriesInAclW(uint cCountOfExplicitEntries, ref EXPLICIT_ACCESS_W pListOfExplicitEntries, IntPtr OldAcl, out IntPtr NewAcl);

    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(IntPtr handle, int ObjectType, uint SecurityInfo, IntPtr psidOwner, IntPtr psidGroup, IntPtr pDacl, IntPtr pSacl);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
    private static extern bool GetFileInformationByHandleExBasic(IntPtr hFile, int fileInformationClass, out FILE_BASIC_INFO lpFileInformation, uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandleEx")]
    private static extern bool GetFileInformationByHandleExTag(IntPtr hFile, int fileInformationClass, out FILE_ATTRIBUTE_TAG_INFO lpFileInformation, uint dwBufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(IntPtr hFile, [Out] char[]? lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetFileAttributesW(string lpFileName, uint dwFileAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
